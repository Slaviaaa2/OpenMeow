using System.Runtime.InteropServices;

namespace OpenMeow.Driver;

/// <summary>
/// 仮想デバイス3台(HMD / 左手 / 右手)の <c>ITrackedDeviceServerDriver_005</c> 実装。
/// あわせて HMD 用の <c>IVRDisplayComponent_003</c> と <c>IVRVirtualDisplay_002</c> を提供する。
/// C++ 仮想クラスをエミュレートするため、vtable とオブジェクトはアンマネージドメモリに
/// 手動構築する。デバイスオブジェクトの配置は [vtable ポインタ][int デバイス ID]。
/// </summary>
internal static unsafe class Devices
{
    public const int Hmd = 0;
    public const int Left = 1;
    public const int Right = 2;

    /// <summary>Activate で SteamVR から割り当てられるデバイスインデックス(未割当 = Invalid)。</summary>
    public static readonly uint[] ObjectIds = { VR.InvalidTrackedDeviceIndex, VR.InvalidTrackedDeviceIndex, VR.InvalidTrackedDeviceIndex };

    /// <summary>片手分の入力コンポーネントハンドル(IVRDriverInput で作成)。</summary>
    public struct HandComponents
    {
        public ulong SystemClick, MenuClick, GripClick, TriggerClick, TriggerValue;
        public ulong PadX, PadY, PadClick, PadTouch, Haptic;
    }

    public static HandComponents LeftComponents;
    public static HandComponents RightComponents;

    /// <summary>
    /// HMD の装着センサー(常時 true を送る)。これがないと HMD が 10 秒で Idle になり、
    /// その後の速い移動のたびに Idle→UserInteraction 遷移イベントが発火して
    /// SteamVR がウィンドウを前面化し、ゲームのフルスクリーンを解除してしまう。
    /// </summary>
    public static ulong HmdProximity;

    private static IntPtr _deviceVtable;
    private static IntPtr _displayObject;
    private static IntPtr _virtualDisplayObject;
    private static readonly IntPtr[] _objects = new IntPtr[3];

    // 仮想ディスプレイの論理サイズ。IVRVirtualDisplay を提供するため、
    // デスクトップ上にヘッドセットウィンドウは作られない(拡張モードは
    // コンポジタがフルスクリーン独占を要求し、ゲームとフォーカスを奪い合う)。
    private const int WindowX = 0, WindowY = 0;
    private const uint WindowWidth = 2560, WindowHeight = 720; // バックバッファ = 片目 1280x720(16:9)×2
    private const uint RenderWidth = 1440, RenderHeight = 810; // 片目レンダーターゲット推奨値

    /// <summary>デバイスオブジェクト(vtable 付きアンマネージドポインタ)を取得・遅延生成する。</summary>
    public static IntPtr Get(int id)
    {
        if (_deviceVtable == IntPtr.Zero)
        {
            var vt = (IntPtr*)NativeMemory.Alloc(6 * (nuint)sizeof(IntPtr));
            vt[0] = (IntPtr)(delegate* unmanaged<IntPtr, uint, int>)&Activate;
            vt[1] = (IntPtr)(delegate* unmanaged<IntPtr, void>)&Deactivate;
            vt[2] = (IntPtr)(delegate* unmanaged<IntPtr, void>)&EnterStandby;
            vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, byte*, IntPtr>)&GetComponent;
            vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, byte*, byte*, uint, void>)&DebugRequest;
            vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&GetPoseStub;
            _deviceVtable = (IntPtr)vt;
        }
        if (_objects[id] == IntPtr.Zero)
        {
            var obj = (IntPtr*)NativeMemory.Alloc(2 * (nuint)sizeof(IntPtr));
            obj[0] = _deviceVtable;
            *(int*)(obj + 1) = id;
            _objects[id] = (IntPtr)obj;
        }
        return _objects[id];
    }

    private static int IdOf(IntPtr thisPtr) => *(int*)((IntPtr*)thisPtr + 1);

    [UnmanagedCallersOnly]
    private static int Activate(IntPtr thisPtr, uint objectId)
    {
        int id = IdOf(thisPtr);
        Log.Write($"Activate device={id} objectId={objectId}");
        ObjectIds[id] = objectId;
        ulong container = Properties.ContainerOf(objectId);

        Properties.SetString(container, VR.Prop_TrackingSystemName_String, "openmeow");
        Properties.SetString(container, VR.Prop_ManufacturerName_String, "OpenMeow");

        if (id == Hmd)
        {
            Properties.SetString(container, VR.Prop_ModelNumber_String, "OpenMeow Virtual HMD");
            Properties.SetString(container, VR.Prop_SerialNumber_String, "OMEOW-HMD-001");
            Properties.SetString(container, VR.Prop_RenderModelName_String, "generic_hmd");
            Properties.SetFloat(container, VR.Prop_DisplayFrequency_Float, 90f);
            Properties.SetFloat(container, VR.Prop_UserIpdMeters_Float, 0.063f);
            Properties.SetFloat(container, VR.Prop_UserHeadToEyeDepthMeters_Float, 0f);
            Properties.SetFloat(container, VR.Prop_SecondsFromVsyncToPhotons_Float, 0.011f);
            Properties.SetUint64(container, VR.Prop_CurrentUniverseId_Uint64, 2);
            Properties.SetBool(container, VR.Prop_IsOnDesktop_Bool, false);
            Properties.SetBool(container, VR.Prop_DisplayDebugMode_Bool, false);
            Properties.SetBool(container, VR.Prop_ContainsProximitySensor_Bool, true);
            HmdProximity = DriverInput.CreateBoolean(container, "/proximity");
            DriverInput.UpdateBoolean(HmdProximity, true);

            const float ipd = 0.063f;
            DriverHost.SetDisplayEyeToHead(objectId,
                HmdMatrix34.Translation(-ipd / 2, 0, 0),
                HmdMatrix34.Translation(+ipd / 2, 0, 0));
        }
        else
        {
            bool isLeft = id == Left;
            Properties.SetString(container, VR.Prop_ModelNumber_String, "OpenMeow Virtual Wand");
            Properties.SetString(container, VR.Prop_SerialNumber_String, isLeft ? "OMEOW-CTL-L" : "OMEOW-CTL-R");
            // SteamVR 同梱の Vive ワンドを名乗ることで、各ゲームの既定バインディングを流用する
            Properties.SetString(container, VR.Prop_RenderModelName_String, "vr_controller_vive_1_5");
            Properties.SetString(container, VR.Prop_ControllerType_String, "vive_controller");
            Properties.SetString(container, VR.Prop_InputProfilePath_String, "{htc}/input/vive_controller_profile.json");
            Properties.SetInt32(container, VR.Prop_ControllerRoleHint_Int32,
                isLeft ? VR.TrackedControllerRole_LeftHand : VR.TrackedControllerRole_RightHand);

            ref HandComponents c = ref (isLeft ? ref LeftComponents : ref RightComponents);
            c.SystemClick = DriverInput.CreateBoolean(container, "/input/system/click");
            c.MenuClick = DriverInput.CreateBoolean(container, "/input/application_menu/click");
            c.GripClick = DriverInput.CreateBoolean(container, "/input/grip/click");
            c.TriggerClick = DriverInput.CreateBoolean(container, "/input/trigger/click");
            c.TriggerValue = DriverInput.CreateScalar(container, "/input/trigger/value", VR.VRScalarUnits_NormalizedOneSided);
            c.PadX = DriverInput.CreateScalar(container, "/input/trackpad/x", VR.VRScalarUnits_NormalizedTwoSided);
            c.PadY = DriverInput.CreateScalar(container, "/input/trackpad/y", VR.VRScalarUnits_NormalizedTwoSided);
            c.PadClick = DriverInput.CreateBoolean(container, "/input/trackpad/click");
            c.PadTouch = DriverInput.CreateBoolean(container, "/input/trackpad/touch");
            c.Haptic = DriverInput.CreateHaptic(container, "/output/haptic");
        }
        return VR.VRInitError_None;
    }

    [UnmanagedCallersOnly]
    private static void Deactivate(IntPtr thisPtr)
    {
        int id = IdOf(thisPtr);
        Log.Write($"Deactivate device={id}");
        ObjectIds[id] = VR.InvalidTrackedDeviceIndex;
    }

    [UnmanagedCallersOnly]
    private static void EnterStandby(IntPtr thisPtr) { }

    [UnmanagedCallersOnly]
    private static IntPtr GetComponent(IntPtr thisPtr, byte* nameAndVersion)
    {
        string requested = Vtbl.ReadAnsi(nameAndVersion);
        if (IdOf(thisPtr) == Hmd)
        {
            if (requested == VR.IVRDisplayComponent_Version) return GetDisplayComponent();
            if (requested == VR.IVRVirtualDisplay_Version) return GetVirtualDisplay();
        }
        return IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static void DebugRequest(IntPtr thisPtr, byte* request, byte* responseBuffer, uint responseBufferSize)
    {
        if (responseBufferSize > 0) responseBuffer[0] = 0;
    }

    // GetPose はヘッダに「呼ばれない」と明記されているスロット。
    // 値返し構造体の x64 MSVC ABI(this の次に隠し戻り値ポインタ)に合わせた形だけのスタブ。
    [UnmanagedCallersOnly]
    private static IntPtr GetPoseStub(IntPtr thisPtr, IntPtr retBuf) => retBuf;

    // --- IVRDisplayComponent_003 ---

    private static IntPtr GetDisplayComponent()
    {
        if (_displayObject == IntPtr.Zero)
        {
            var vt = (IntPtr*)NativeMemory.Alloc(8 * (nuint)sizeof(IntPtr));
            vt[0] = (IntPtr)(delegate* unmanaged<IntPtr, int*, int*, uint*, uint*, void>)&GetWindowBounds;
            vt[1] = (IntPtr)(delegate* unmanaged<IntPtr, byte>)&IsDisplayOnDesktop;
            vt[2] = (IntPtr)(delegate* unmanaged<IntPtr, byte>)&IsDisplayRealDisplay;
            vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, uint*, uint*, void>)&GetRecommendedRenderTargetSize;
            vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, int, uint*, uint*, uint*, uint*, void>)&GetEyeOutputViewport;
            vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, int, float*, float*, float*, float*, void>)&GetProjectionRaw;
            vt[6] = (IntPtr)(delegate* unmanaged<IntPtr, DistortionCoordinates*, int, float, float, IntPtr>)&ComputeDistortion;
            vt[7] = (IntPtr)(delegate* unmanaged<IntPtr, float*, int, uint, float, float, byte>)&ComputeInverseDistortion;
            var obj = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
            obj[0] = (IntPtr)vt;
            _displayObject = (IntPtr)obj;
        }
        return _displayObject;
    }

    [UnmanagedCallersOnly]
    private static void GetWindowBounds(IntPtr thisPtr, int* x, int* y, uint* width, uint* height)
    {
        *x = WindowX; *y = WindowY; *width = WindowWidth; *height = WindowHeight;
    }

    [UnmanagedCallersOnly]
    private static byte IsDisplayOnDesktop(IntPtr thisPtr) => 0;

    [UnmanagedCallersOnly]
    private static byte IsDisplayRealDisplay(IntPtr thisPtr) => 0;

    [UnmanagedCallersOnly]
    private static void GetRecommendedRenderTargetSize(IntPtr thisPtr, uint* width, uint* height)
    {
        *width = RenderWidth; *height = RenderHeight;
    }

    [UnmanagedCallersOnly]
    private static void GetEyeOutputViewport(IntPtr thisPtr, int eye, uint* x, uint* y, uint* width, uint* height)
    {
        *width = WindowWidth / 2;
        *height = WindowHeight;
        *x = eye == VR.Eye_Left ? 0u : WindowWidth / 2;
        *y = 0;
    }

    [UnmanagedCallersOnly]
    private static void GetProjectionRaw(IntPtr thisPtr, int eye, float* left, float* right, float* top, float* bottom)
    {
        // tan(半視野角)。±1.0(=90°)だとズームしたように巨大に見える。
        // 水平 110°、垂直はバックバッファの 16:9 に合わせて ~77.5°(デスクトップ表示向き)。
        const float h = 1.428f;
        const float v = h * 720f / 1280f;
        *left = -h; *right = h; *top = -v; *bottom = v;
    }

    // 歪み補正なし(恒等写像)。値返し構造体は x64 MSVC メンバ関数 ABI で
    // this の次に隠し戻り値ポインタが入るため、第2引数で受けてそのまま返す。
    [UnmanagedCallersOnly]
    private static IntPtr ComputeDistortion(IntPtr thisPtr, DistortionCoordinates* result, int eye, float u, float v)
    {
        result->RedU = u; result->RedV = v;
        result->GreenU = u; result->GreenV = v;
        result->BlueU = u; result->BlueV = v;
        return (IntPtr)result;
    }

    [UnmanagedCallersOnly]
    private static byte ComputeInverseDistortion(IntPtr thisPtr, float* result, int eye, uint channel, float u, float v)
    {
        result[0] = u; result[1] = v;
        return 1;
    }

    // --- IVRVirtualDisplay_002 ---
    // 実ディスプレイが存在しないため、vsync は Stopwatch ベースの 90Hz 擬似クロックで再現する。
    // Present で届く合成済みフレームは FrameMirror がコントロールパネルへ転送する。

    private static readonly System.Diagnostics.Stopwatch VsyncClock = System.Diagnostics.Stopwatch.StartNew();
    private const double VsyncPeriod = 1.0 / 90.0;

    private static IntPtr GetVirtualDisplay()
    {
        if (_virtualDisplayObject == IntPtr.Zero)
        {
            var vt = (IntPtr*)NativeMemory.Alloc(3 * (nuint)sizeof(IntPtr));
            vt[0] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, void>)&Present;
            vt[1] = (IntPtr)(delegate* unmanaged<IntPtr, void>)&WaitForPresent;
            vt[2] = (IntPtr)(delegate* unmanaged<IntPtr, float*, ulong*, byte>)&GetTimeSinceLastVsync;
            var obj = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
            obj[0] = (IntPtr)vt;
            _virtualDisplayObject = (IntPtr)obj;
        }
        return _virtualDisplayObject;
    }

    [UnmanagedCallersOnly]
    private static void Present(IntPtr thisPtr, IntPtr presentInfo, uint presentInfoSize)
        => FrameMirror.OnPresent(presentInfo);

    [UnmanagedCallersOnly]
    private static void WaitForPresent(IntPtr thisPtr)
    {
        double now = VsyncClock.Elapsed.TotalSeconds;
        double next = (Math.Floor(now / VsyncPeriod) + 1) * VsyncPeriod;
        int ms = (int)((next - now) * 1000);
        if (ms > 0) Thread.Sleep(ms);
    }

    [UnmanagedCallersOnly]
    private static byte GetTimeSinceLastVsync(IntPtr thisPtr, float* secondsSinceLastVsync, ulong* frameCounter)
    {
        double now = VsyncClock.Elapsed.TotalSeconds;
        double frame = Math.Floor(now / VsyncPeriod);
        *secondsSinceLastVsync = (float)(now - frame * VsyncPeriod);
        *frameCounter = (ulong)frame;
        return 1;
    }
}
