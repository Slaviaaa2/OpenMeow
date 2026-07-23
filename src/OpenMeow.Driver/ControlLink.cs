using System.IO.MemoryMappedFiles;

namespace OpenMeow.Driver;

/// <summary>コントロールパネルから届く1フレーム分の入力サンプル。</summary>
internal sealed class ControlSample
{
    /// <summary>パネルが生きているか(heartbeat が 1 秒以内)。false なら他フィールドは無効。</summary>
    public bool Fresh;

    /// <summary>パネルがマウスをキャプチャ中か。</summary>
    public bool Active;

    /// <summary>前回サンプルからのマウス移動量 [px] とホイール回転量 [ノッチ]。</summary>
    public double MouseDx, MouseDy, WheelDelta;

    /// <summary>マウス左 / 右ボタンの現在状態。</summary>
    public bool Lmb, Rmb;
}

/// <summary>
/// コントロールパネル(OpenMeowOverlay)との共有メモリリンクの読み手。
/// レイアウト(64バイト、8バイト境界、単一書き手・単一読み手):
///   0: long   heartbeat (UtcNow.Ticks)   8: long   active
///  16: double mouseX 累積 [px]          24: double mouseY 累積 [px]
///  32: double wheel 累積 [ノッチ]        40: long   LMB    48: long RMB
/// マウス/ホイールは累積値で受け渡し、読み手が前回値との差分を取る
/// (「読んだらリセット」方式で起きる書き手との競合を避けるため)。
/// </summary>
internal static class ControlLink
{
    public const string MapName = "Local\\OpenMeowControl";

    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static double _lastX, _lastY, _lastWheel;
    private static bool _wasFresh;

    private static bool EnsureOpen()
    {
        if (_view != null) return true;
        try
        {
#pragma warning disable CA1416 // NativeAOT driver is win-x64 by definition.
            _mmf = MemoryMappedFile.CreateOrOpen(MapName, 64);
#pragma warning restore CA1416
            _view = _mmf.CreateViewAccessor(0, 64);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static ControlSample Sample()
    {
        var s = new ControlSample();
        if (!EnsureOpen() || _view == null) return s;

        long heartbeat = _view.ReadInt64(0);
        s.Fresh = heartbeat != 0 && (DateTime.UtcNow.Ticks - heartbeat) < TimeSpan.TicksPerSecond;
        if (s.Fresh != _wasFresh)
        {
            Log.Write($"control link {(s.Fresh ? "connected" : "lost")}");
            _wasFresh = s.Fresh;
        }
        double x = _view.ReadDouble(16);
        double y = _view.ReadDouble(24);
        double w = _view.ReadDouble(32);
        if (!s.Fresh)
        {
            // 接続断中にオーバーレイが再起動して累積値を初期化しても、
            // 再接続時に巨大なマウス移動として扱わないよう基準値を更新する。
            _lastX = x; _lastY = y; _lastWheel = w;
            return s;
        }

        s.Active = _view.ReadInt64(8) != 0;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w)) return s;
        s.MouseDx = x - _lastX;
        s.MouseDy = y - _lastY;
        s.WheelDelta = w - _lastWheel;
        if (!double.IsFinite(s.MouseDx) || !double.IsFinite(s.MouseDy) || !double.IsFinite(s.WheelDelta) ||
            Math.Abs(s.MouseDx) > 512 || Math.Abs(s.MouseDy) > 512 || Math.Abs(s.WheelDelta) > 32)
        {
            _lastX = x; _lastY = y; _lastWheel = w;
            return s;
        }
        _lastX = x; _lastY = y; _lastWheel = w;
        s.Lmb = _view.ReadInt64(40) != 0;
        s.Rmb = _view.ReadInt64(48) != 0;
        return s;
    }

    public static void Cleanup()
    {
        _view?.Dispose();
        _view = null;
        _mmf?.Dispose();
        _mmf = null;
        _lastX = _lastY = _lastWheel = 0;
        _wasFresh = false;
    }
}
