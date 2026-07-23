using System.Diagnostics;
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
        long minRowBytes = (long)width * 4;
        if (width <= 0 || height <= 0 || width % 2 != 0 || rowBytes < minRowBytes ||
            (long)rowBytes > MaxPixelBytes / height) return false;

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
    // SteamVR の Steam アプリ ID(steam://rungameid で起動する)
    private const int SteamVrAppId = 250820;

    [DllImport("user32.dll")] private static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private readonly ControlChannel _channel = new();
    private readonly FrameReceiver _receiver = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Label _state = new();
    private readonly Panel _help = new();
    private readonly ViewPanel _viewPanel = new();
    private readonly Button _launchBtn;
    private readonly Button _helpBtn;

    private bool _captured;
    private bool _invX, _invY;
    private bool _steamVrRunning;
    private DateTime _launchAt = DateTime.MinValue;
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
        MinimumSize = new Size(620, 460);
        Size = new Size(960, 760);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);

        // ── ヘッダ(状態表示 + 起動/説明トグルボタン)──
        var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.FromArgb(18, 20, 26) };

        _launchBtn = MakeButton("SteamVR を起動", Color.FromArgb(59, 130, 246));
        _launchBtn.Click += (_, _) => LaunchSteamVr();
        _helpBtn = MakeButton("操作説明を隠す", Color.FromArgb(55, 58, 68));
        _helpBtn.Click += (_, _) => ToggleHelp();

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 9, 8, 0),
            BackColor = Color.Transparent,
        };
        bar.Controls.Add(_launchBtn);
        bar.Controls.Add(_helpBtn);

        _state.Dock = DockStyle.Fill;
        _state.Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold);
        _state.ForeColor = Color.Orange;
        _state.TextAlign = ContentAlignment.MiddleLeft;
        _state.Padding = new Padding(12, 0, 0, 0);

        header.Controls.Add(_state);
        header.Controls.Add(bar);

        // ── 操作説明(2カラムに整理)──
        _help.Dock = DockStyle.Bottom;
        _help.Height = 176;
        _help.BackColor = Color.FromArgb(20, 22, 28);
        _help.Padding = new Padding(2, 6, 2, 6);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.Controls.Add(MakeHelpLabel(
            "◆ 視点・トリガー\n" +
            "マウス移動 … 見回し(手は視線へ自動照準)\n" +
            "左クリック … トリガー / 右クリック … グリップ\n" +
            "\n" +
            "◆ 手の操作(押している間だけ有効)\n" +
            "Space … 右手の位置(ホイール=奥行き)\n" +
            "中クリック … 右手の手首(ホイール=横倒し)\n" +
            "X1(または Alt) … 左手の位置 / X2 … 左手の手首\n" +
            "Tab … 左パッド(歩行) / R … 右パッド(旋回)\n" +
            "※ パッド系 +左クリックで押し込み"), 0, 0);
        grid.Controls.Add(MakeHelpLabel(
            "◆ 移動・視点\n" +
            "WASD … 移動 / Q・E … 下降・上昇\n" +
            "矢印 … 頭(手首ホールド中は手)の微回転\n" +
            "Shift … 高速 / Ctrl … 低速\n" +
            "BackSpace … リセット / ESC … 操作解除\n" +
            "\n" +
            "◆ ボタン・トグル\n" +
            "Y・B … 右手・左手グリップ保持\n" +
            "F5・F6 … 左右・上下 反転\n" +
            "左手 … Z X C V + T F G H + F7\n" +
            "右手 … U O P M + I J K L + F8"), 1, 0);
        _help.Controls.Add(grid);

        _viewPanel.Dock = DockStyle.Fill;
        _viewPanel.Receiver = _receiver;
        _viewPanel.Cursor = Cursors.Cross;

        Controls.Add(_viewPanel);
        Controls.Add(_help);
        Controls.Add(header);

        _viewPanel.MouseDown += (_, e) => { if (!_captured && e.Button == MouseButtons.Left) StartCapture(); };
        _viewPanel.MouseWheel += (_, e) => { if (_captured) _channel.Wheel += e.Delta / 120.0; };
        MouseWheel += (_, e) => { if (_captured) _channel.Wheel += e.Delta / 120.0; };
        FormClosed += (_, _) => { ReleaseCapture(); _channel.Dispose(); _receiver.Dispose(); };
        Resize += (_, _) => { if (_captured) ClipToView(); };

        UpdateIdleText();
        UpdateStateLabel();

        _timer.Interval = 8; // 入力ポンプ ~120Hz、描画はフレーム更新時のみ
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
    }

    private static Button MakeButton(string text, Color accent)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = accent,
            AutoSize = true,
            Height = 30,
            Margin = new Padding(6, 0, 0, 0),
            Padding = new Padding(12, 4, 12, 4),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(accent, 0.15f);
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(accent, 0.1f);
        return b;
    }

    private static Label MakeHelpLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Font = new Font("Yu Gothic UI", 9f),
        ForeColor = Color.Gainsboro,
        Padding = new Padding(10, 2, 6, 2),
    };

    private void ToggleHelp()
    {
        _help.Visible = !_help.Visible;
        _helpBtn.Text = _help.Visible ? "操作説明を隠す" : "操作説明を表示";
    }

    private static bool IsSteamVrRunning()
    {
        foreach (string name in new[] { "vrmonitor", "vrserver" })
        {
            var procs = Process.GetProcessesByName(name);
            try { if (procs.Length > 0) return true; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        return false;
    }

    private void LaunchSteamVr()
    {
        if (IsSteamVrRunning()) return;
        try
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamVrAppId}") { UseShellExecute = true });
            _launchAt = DateTime.UtcNow;
            _launchBtn.Enabled = false;
            _launchBtn.Text = "起動中…";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "SteamVR を起動できませんでした。Steam がインストールされているか確認してください。\n\n" + ex.Message,
                "OpenMeow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateIdleText()
        => _viewPanel.IdleText = _steamVrRunning
            ? "映像待機中… 数秒で映ります(映像をクリックで操作開始)"
            : "右上の『SteamVR を起動』を押してください(起動後、映像が届いたらクリックで操作開始)";

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

        if (++_stateTick >= 50) // ~400ms ごとに反転状態と SteamVR 状態を反映
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

            // フレームが届いている = 確実に起動中。プロセスも併せて確認する。
            // ボタンは毎回実状態へ追従させる(起動に失敗しても固まらないよう、
            // クリック後 10 秒だけ「起動中…」表示にして以降は元へ戻す)。
            bool vr = _receiver.Frame != null || IsSteamVrRunning();
            bool launching = !vr && (DateTime.UtcNow - _launchAt) < TimeSpan.FromSeconds(10);
            _launchBtn.Enabled = !vr && !launching;
            _launchBtn.Text = vr ? "SteamVR 起動済み" : launching ? "起動中…" : "SteamVR を起動";
            if (vr != _steamVrRunning)
            {
                _steamVrRunning = vr;
                UpdateIdleText();
                if (!_captured) _viewPanel.Invalidate();
            }
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
