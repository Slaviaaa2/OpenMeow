using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace OpenMeow.Overlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ControlForm());
    }
}

/// <summary>
/// ドライバへの共有メモリ書き込み。レイアウトは ControlLink.cs(ドライバ側)と一致させること。
/// </summary>
internal sealed class ControlChannel : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;

    public double MouseX, MouseY, Wheel;
    public bool Lmb, Rmb, Active;

    public ControlChannel()
    {
        _mmf = MemoryMappedFile.CreateOrOpen("Local\\OpenMeowControl", 64);
        _view = _mmf.CreateViewAccessor(0, 64);
    }

    public void Flush()
    {
        _view.Write(8, Active ? 1L : 0L);
        _view.Write(16, MouseX);
        _view.Write(24, MouseY);
        _view.Write(32, Wheel);
        _view.Write(40, Lmb ? 1L : 0L);
        _view.Write(48, Rmb ? 1L : 0L);
        _view.Write(0, DateTime.UtcNow.Ticks); // heartbeat は最後(他フィールド確定後)
    }

    public void Dispose()
    {
        try { _view.Write(0, 0L); } catch { }
        _view.Dispose();
        _mmf.Dispose();
    }
}

/// <summary>
/// ドライバの FrameMirror が書く合成済みフレーム(共有メモリ)の読み手。
/// seq が偶数かつ読取前後で一致したときだけ採用する。
/// </summary>
internal sealed unsafe class FrameReceiver : IDisposable
{
    private const int HeaderBytes = 32;
    private const int MaxPixelBytes = 1920 * 1200 * 4;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _ptr;
    private long _lastSeq;

    public Bitmap? Frame;      // 左目のみ(表示用)
    public long FrameCount;

    public FrameReceiver()
    {
        _mmf = MemoryMappedFile.CreateOrOpen("Local\\OpenMeowFrame", HeaderBytes + MaxPixelBytes);
        _view = _mmf.CreateViewAccessor(0, HeaderBytes + MaxPixelBytes);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _ptr = p;
    }

    /// <summary>新しいフレームがあれば Frame を更新して true。</summary>
    public bool Poll()
    {
        long seq = *(long*)_ptr;
        if (seq == 0 || seq == _lastSeq || (seq & 1) != 0) return false;

        int width = *(int*)(_ptr + 8);
        int height = *(int*)(_ptr + 12);
        int rowBytes = *(int*)(_ptr + 16);
        int format = *(int*)(_ptr + 20);
        if (width <= 0 || height <= 0 || (long)rowBytes * height > MaxPixelBytes) return false;

        // 左右の目が横並びなので左半分だけ取り出す
        int eyeWidth = width / 2;
        var bmp = Frame;
        if (bmp == null || bmp.Width != eyeWidth || bmp.Height != height)
        {
            Frame?.Dispose();
            bmp = Frame = new Bitmap(eyeWidth, height, PixelFormat.Format32bppRgb);
        }

        // DXGI 28/29 = RGBA(要 R/B 入替)、87/91 = BGRA(そのまま)
        bool swizzle = format is 28 or 29;

        var locked = bmp.LockBits(new Rectangle(0, 0, eyeWidth, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            byte* src = _ptr + HeaderBytes;
            for (int y = 0; y < height; y++)
            {
                uint* s = (uint*)(src + (long)y * rowBytes);
                uint* d = (uint*)((byte*)locked.Scan0 + (long)y * locked.Stride);
                if (swizzle)
                {
                    for (int x = 0; x < eyeWidth; x++)
                    {
                        uint v = s[x];
                        d[x] = (v & 0xFF00FF00u) | ((v & 0xFFu) << 16) | ((v >> 16) & 0xFFu);
                    }
                }
                else
                {
                    Buffer.MemoryCopy(s, d, (long)eyeWidth * 4, (long)eyeWidth * 4);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(locked);
        }

        // 書き込みと競合していないか確認してから採用
        if (*(long*)_ptr != seq) return false;
        _lastSeq = seq;
        FrameCount = *(long*)(_ptr + 24);
        return true;
    }

    public void Dispose()
    {
        Frame?.Dispose();
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}

/// <summary>フレームをアスペクト維持で描く、クリック=操作開始のビューパネル。</summary>
internal sealed class ViewPanel : Panel
{
    public FrameReceiver? Receiver;
    public string IdleText = "";

    public ViewPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(10, 11, 14);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var frame = Receiver?.Frame;
        if (frame == null)
        {
            TextRenderer.DrawText(e.Graphics, IdleText, Font, ClientRectangle, Color.DimGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        double scale = Math.Min((double)ClientSize.Width / frame.Width, (double)ClientSize.Height / frame.Height);
        int w = (int)(frame.Width * scale), h = (int)(frame.Height * scale);
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        e.Graphics.DrawImage(frame, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }
}

/// <summary>
/// OpenMeow コントロールパネル。
/// ドライバ直結のライブビュー(合成済みフレーム)を表示し、
/// クリックでマウスをキャプチャして FPS 風に操作、ESC で解放。
/// </summary>
internal sealed class ControlForm : Form
{
    [DllImport("user32.dll")] private static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private readonly ControlChannel _channel = new();
    private readonly FrameReceiver _receiver = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Label _state = new();
    private readonly Label _help = new();
    private readonly ViewPanel _viewPanel = new();

    private bool _captured;
    private bool _invX, _invY;
    private int _stateTick;
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenMeow", "state.txt");

    public ControlForm()
    {
        Text = "OpenMeow コントロール";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(24, 26, 32);
        ForeColor = Color.Gainsboro;
        MinimumSize = new Size(560, 420);
        Size = new Size(960, 740);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);

        _state.Dock = DockStyle.Top;
        _state.Height = 30;
        _state.Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold);
        _state.ForeColor = Color.Orange;
        _state.Padding = new Padding(8, 6, 0, 0);

        _help.Dock = DockStyle.Bottom;
        _help.AutoSize = false;
        _help.Height = 140;
        _help.Font = new Font("Yu Gothic UI", 9f);
        _help.Padding = new Padding(8, 4, 0, 0);
        _help.Text =
            "マウス 見回し(手は視線に自動照準)   左クリック トリガー   右クリック グリップ\n" +
            "【右手】Space+マウス=位置(ホイール=奥行き)   中クリック+マウス=手首(ホイール=横倒し)\n" +
            "【左手】サイド奥(X1)+マウス=位置   サイド手前(X2)+マウス=手首   ※Alt=X1 の代用\n" +
            "左手系を押している間は 左/右クリック=左手のトリガー/グリップ\n" +
            "【スティック】Tab+マウス=左パッド(歩行)   R+マウス=右パッド(旋回)   +左クリック=押し込み\n" +
            "Y/B 右手/左手グリップ保持トグル   F5 左右反転   F6 上下反転\n" +
            "WASD 移動  Q/E 昇降  矢印 頭の微回転  Shift 高速  Ctrl 低速  BackSpace リセット  ESC 解除\n" +
            "左手キー: Z/X/C/V + T/F/G/H + F7    右手キー: U/O/P/M + I/J/K/L + F8";

        _viewPanel.Dock = DockStyle.Fill;
        _viewPanel.Receiver = _receiver;
        _viewPanel.IdleText = "映像待機中… SteamVR が起動していれば数秒で映ります(クリックで操作開始)";
        _viewPanel.Cursor = Cursors.Cross;

        Controls.Add(_viewPanel);
        Controls.Add(_help);
        Controls.Add(_state);

        _viewPanel.MouseDown += (_, e) => { if (!_captured && e.Button == MouseButtons.Left) StartCapture(); };
        _state.Click += (_, _) => { if (!_captured) StartCapture(); };
        _viewPanel.MouseWheel += (_, e) => { if (_captured) _channel.Wheel += e.Delta / 120.0; };
        MouseWheel += (_, e) => { if (_captured) _channel.Wheel += e.Delta / 120.0; };
        FormClosed += (_, _) => { ReleaseCapture(); _channel.Dispose(); _receiver.Dispose(); };
        Resize += (_, _) => { if (_captured) ClipToView(); };

        UpdateStateLabel();

        _timer.Interval = 8; // 入力ポンプ ~120Hz、描画はフレーム更新時のみ
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
    }

    private Point ViewCenterScreen()
        => _viewPanel.PointToScreen(new Point(_viewPanel.ClientSize.Width / 2, _viewPanel.ClientSize.Height / 2));

    private void ClipToView()
    {
        var r = _viewPanel.RectangleToScreen(_viewPanel.ClientRectangle);
        var rect = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        ClipCursor(ref rect);
    }

    private void StartCapture()
    {
        _captured = true;
        _channel.Active = true;
        Cursor.Hide();
        Cursor.Position = ViewCenterScreen();
        ClipToView();
        UpdateStateLabel();
    }

    private void ReleaseCapture()
    {
        if (!_captured) return;
        _captured = false;
        _channel.Active = false;
        _channel.Lmb = _channel.Rmb = false;
        ClipCursor(IntPtr.Zero);
        Cursor.Show();
        UpdateStateLabel();
    }

    private static bool KeyDown_(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// 入力ポンプ(~120Hz)。マウスは「カーソルを中央へ戻しつつ移動量を回収」する方式で、
    /// ボタンと ESC はフォーカスに依存しないグローバルキー状態で判定する。
    /// </summary>
    private void Pump()
    {
        if (_captured)
        {
            if (KeyDown_(0x1B)) { ReleaseCapture(); }
            else
            {
                _channel.Lmb = KeyDown_(0x01); // VK_LBUTTON
                _channel.Rmb = KeyDown_(0x02); // VK_RBUTTON

                Point center = ViewCenterScreen();
                Point pos = Cursor.Position;
                _channel.MouseX += pos.X - center.X;
                _channel.MouseY += pos.Y - center.Y;
                Cursor.Position = center;
            }
        }
        _channel.Flush();

        if (++_stateTick >= 50) // ~400ms ごとに反転状態を反映
        {
            _stateTick = 0;
            try
            {
                bool ix = false, iy = false;
                foreach (string line in File.ReadAllLines(_statePath))
                {
                    if (line.StartsWith("invertX=")) ix = line.EndsWith("1");
                    else if (line.StartsWith("invertY=")) iy = line.EndsWith("1");
                }
                if (ix != _invX || iy != _invY) { _invX = ix; _invY = iy; UpdateStateLabel(); }
            }
            catch { }
        }

        if (_receiver.Poll()) _viewPanel.Invalidate();
    }

    private void UpdateStateLabel()
    {
        string inv = (_invX, _invY) switch
        {
            (true, true) => "   [反転: 左右+上下]",
            (true, false) => "   [反転: 左右]",
            (false, true) => "   [反転: 上下]",
            _ => "",
        };
        if (_captured)
        {
            _state.Text = "● 操作中   ESC で解除" + inv;
            _state.ForeColor = Color.MediumSpringGreen;
        }
        else
        {
            _state.Text = "○ 待機中 — 映像をクリックで操作開始" + inv;
            _state.ForeColor = Color.Orange;
        }
    }
}
