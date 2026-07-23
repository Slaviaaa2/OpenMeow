using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace OpenMeow.Driver;

/// <summary>
/// IVRVirtualDisplay::Present で届く合成済みバックバッファ(D3D11 共有テクスチャ)を開き、
/// CPU に読み戻して共有メモリへ流す。コントロールパネルがこれを描画する。
/// vtable インデックスは Windows SDK 10.0.26100.0 の d3d11.h C インターフェースで確認済み:
///   ID3D11Device: 5=CreateTexture2D, 28=OpenSharedResource
///   ID3D11DeviceContext: 14=Map, 15=Unmap, 47=CopyResource
///   ID3D11Texture2D: 2=Release, 10=GetDesc
/// 共有メモリ "Local\OpenMeowFrame" レイアウト:
///   0:long seq(書込中は奇数)  8:int width  12:int height  16:int rowBytes
///   20:int dxgiFormat  24:long frameCount  32〜: ピクセル(BGRA/RGBA 生データ)
/// </summary>
internal static unsafe class FrameMirror
{
    private const int HeaderBytes = 32;
    private const int MaxPixelBytes = 1920 * 1200 * 4;
    private const int FrameDivider = 2; // 90Hz の 1/2 = 45fps

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        IntPtr* device, int* featureLevel, IntPtr* context);

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags;
    }

    private static IntPtr _device, _context;
    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static byte* _viewPtr;
    private static IntPtr _staging;
    private static Texture2DDesc _stagingDesc;
    private static readonly Dictionary<ulong, (IntPtr Tex, IntPtr Mutex)> _textureCache = new();
    private static long _frameCount;
    private static long _seq;
    private static bool _failed;
    private static bool _loggedFirst;
    private static bool _loggedAcquireFail;

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDXGIKeyedMutex = new("9d8e1289-d7b3-465f-8126-250e349af85d");

    private static bool EnsureInit()
    {
        if (_failed) return false;
        if (_device != IntPtr.Zero && _view != null) return true;
        try
        {
            if (_view == null)
            {
#pragma warning disable CA1416 // NativeAOT driver is win-x64 by definition.
                _mmf = MemoryMappedFile.CreateOrOpen("Local\\OpenMeowFrame", HeaderBytes + MaxPixelBytes);
#pragma warning restore CA1416
                _view = _mmf.CreateViewAccessor(0, HeaderBytes + MaxPixelBytes);
                byte* p = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _viewPtr = p;
            }
            if (_device == IntPtr.Zero)
            {
                IntPtr dev, ctx;
                int fl;
                // D3D_DRIVER_TYPE_HARDWARE=1, D3D11_SDK_VERSION=7
                int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, &dev, &fl, &ctx);
                if (hr < 0)
                {
                    Log.Write($"FrameMirror: D3D11CreateDevice failed 0x{hr:x8}");
                    _failed = true;
                    return false;
                }
                _device = dev;
                _context = ctx;
                Log.Write($"FrameMirror: D3D11 device ready (featureLevel=0x{fl:x})");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"FrameMirror init failed: {ex.Message}");
            _failed = true;
            return false;
        }
    }

    private static (IntPtr Tex, IntPtr Mutex) OpenShared(ulong handle)
    {
        if (_textureCache.TryGetValue(handle, out var cached)) return cached;
        var open = (delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl.Slot(_device, 28);
        IntPtr tex = IntPtr.Zero;
        Guid iid = IID_ID3D11Texture2D;
        int hr = open(_device, (IntPtr)(long)handle, &iid, &tex);
        if (hr < 0)
        {
            if (!_loggedFirst) { Log.Write($"FrameMirror: OpenSharedResource failed 0x{hr:x8}"); _loggedFirst = true; }
            return default;
        }

        // 共有テクスチャが keyed mutex 付きなら、Acquire なしの読み出しは黒/古い内容になる
        var qi = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Vtbl.Slot(tex, 0);
        IntPtr mutex = IntPtr.Zero;
        Guid miid = IID_IDXGIKeyedMutex;
        qi(tex, &miid, &mutex); // 失敗なら mutex は Zero のまま

        if (_textureCache.Count > 8) // スワップチェーン入れ替え時の古いハンドルを掃除
        {
            foreach (var old in _textureCache.Values) { Release(old.Mutex); Release(old.Tex); }
            _textureCache.Clear();
        }
        _textureCache[handle] = (tex, mutex);
        return (tex, mutex);
    }

    private static void Release(IntPtr unknown)
    {
        if (unknown == IntPtr.Zero) return;
        var release = (delegate* unmanaged<IntPtr, uint>)Vtbl.Slot(unknown, 2);
        release(unknown);
    }

    private static bool EnsureStaging(IntPtr sourceTex)
    {
        var getDesc = (delegate* unmanaged<IntPtr, Texture2DDesc*, void>)Vtbl.Slot(sourceTex, 10);
        Texture2DDesc desc;
        getDesc(sourceTex, &desc);
        if (_staging != IntPtr.Zero && desc.Width == _stagingDesc.Width &&
            desc.Height == _stagingDesc.Height && desc.Format == _stagingDesc.Format)
            return true;

        if (_staging != IntPtr.Zero) { Release(_staging); _staging = IntPtr.Zero; }
        ulong pixelBytes = (ulong)desc.Width * desc.Height * 4;
        if (desc.Width == 0 || desc.Height == 0 || pixelBytes > MaxPixelBytes)
        {
            if (!_loggedFirst) { Log.Write($"FrameMirror: frame too large {desc.Width}x{desc.Height}"); _loggedFirst = true; }
            return false;
        }

        var staging = desc;
        staging.MipLevels = 1;
        staging.ArraySize = 1;
        staging.SampleCount = 1;
        staging.SampleQuality = 0;
        staging.Usage = 3;          // D3D11_USAGE_STAGING
        staging.BindFlags = 0;
        staging.CpuAccessFlags = 0x20000; // D3D11_CPU_ACCESS_READ
        staging.MiscFlags = 0;

        var create = (delegate* unmanaged<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Vtbl.Slot(_device, 5);
        IntPtr tex = IntPtr.Zero;
        int hr = create(_device, &staging, IntPtr.Zero, &tex);
        if (hr < 0)
        {
            Log.Write($"FrameMirror: CreateTexture2D(staging) failed 0x{hr:x8}");
            _failed = true;
            return false;
        }
        _staging = tex;
        _stagingDesc = desc;
        if (!_loggedFirst)
        {
            Log.Write($"FrameMirror: streaming {desc.Width}x{desc.Height} format={desc.Format} " +
                      $"usage={desc.Usage} bind=0x{desc.BindFlags:x} misc=0x{desc.MiscFlags:x}");
            _loggedFirst = true;
        }
        return true;
    }

    /// <summary>Present から毎フレーム呼ばれる。offset 0 に共有テクスチャハンドル。</summary>
    public static void OnPresent(IntPtr presentInfo, uint presentInfoSize)
    {
        _frameCount++;
        if (_frameCount % FrameDivider != 0) return;
        if (presentInfo == IntPtr.Zero || presentInfoSize < sizeof(ulong)) return;
        if (!EnsureInit()) return;

        try
        {
            ulong handle = *(ulong*)presentInfo;
            (IntPtr tex, IntPtr mutex) = OpenShared(handle);
            if (tex == IntPtr.Zero || !EnsureStaging(tex)) return;

            bool acquired = false;
            if (mutex != IntPtr.Zero)
            {
                // IDXGIKeyedMutex vtable: 8=AcquireSync(key, ms), 9=ReleaseSync(key)
                var acquire = (delegate* unmanaged<IntPtr, ulong, uint, int>)Vtbl.Slot(mutex, 8);
                int ahr = acquire(mutex, 0, 4);
                acquired = ahr == 0;
                if (!acquired && !_loggedAcquireFail)
                {
                    Log.Write($"FrameMirror: AcquireSync failed 0x{ahr:x8}");
                    _loggedAcquireFail = true;
                }
                if (!acquired) return;
            }

            bool mapped = false;
            try
            {
                var copy = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Vtbl.Slot(_context, 47);
                copy(_context, _staging, tex);

                // D3D11_MAPPED_SUBRESOURCE { void* pData; uint RowPitch; uint DepthPitch; }
                IntPtr data = IntPtr.Zero;
                uint rowPitch = 0;
                var map = (delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, void**, int>)Vtbl.Slot(_context, 14);
                void* mappedRaw = stackalloc byte[16];
                int hr = map(_context, _staging, 0, 1 /*D3D11_MAP_READ*/, 0, (void**)mappedRaw);
                if (hr < 0) return;
                mapped = true;
                data = *(IntPtr*)mappedRaw;
                rowPitch = *(uint*)((byte*)mappedRaw + 8);

                int width = (int)_stagingDesc.Width, height = (int)_stagingDesc.Height;
                int rowBytes = checked(width * 4);
                if (rowPitch < rowBytes) return;

                Interlocked.Increment(ref _seq);            // 奇数 = 書き込み中
                *(long*)_viewPtr = _seq;
                byte* dst = _viewPtr + HeaderBytes;
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy((byte*)data + (long)y * rowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
                *(int*)(_viewPtr + 8) = width;
                *(int*)(_viewPtr + 12) = height;
                *(int*)(_viewPtr + 16) = rowBytes;
                *(int*)(_viewPtr + 20) = _stagingDesc.Format;
                *(long*)(_viewPtr + 24) = _frameCount;
                Interlocked.Increment(ref _seq);            // 偶数 = 完了
                *(long*)_viewPtr = _seq;
            }
            finally
            {
                if (mapped)
                {
                    var unmap = (delegate* unmanaged<IntPtr, IntPtr, uint, void>)Vtbl.Slot(_context, 15);
                    unmap(_context, _staging, 0);
                }
                if (acquired)
                {
                    var release2 = (delegate* unmanaged<IntPtr, ulong, int>)Vtbl.Slot(mutex, 9);
                    release2(mutex, 0);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"FrameMirror error: {ex.Message}");
        }
    }

    /// <summary>SteamVRの再初期化時にD3D11と共有メモリを解放する。</summary>
    public static void Cleanup()
    {
        foreach (var item in _textureCache.Values)
        {
            Release(item.Mutex);
            Release(item.Tex);
        }
        _textureCache.Clear();
        Release(_staging);
        Release(_context);
        Release(_device);
        _staging = _context = _device = IntPtr.Zero;
        if (_view != null)
        {
            if (_viewPtr != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _view = null;
        }
        _mmf?.Dispose();
        _mmf = null;
        _viewPtr = null;
        _failed = false;
        _frameCount = _seq = 0;
        _loggedFirst = _loggedAcquireFail = false;
    }
}
