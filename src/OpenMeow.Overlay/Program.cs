using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using OpenMeow;

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
        ResizeRedraw = true;
        BackColor = Color.FromArgb(10, 11, 14);
        Font = new Font("Yu Gothic UI", 14f, FontStyle.Bold);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);

        // 最大化中は複数回に分けてサイズが変わり、既存の文字位置が通常の
        // 無効領域から外れることがある。毎回全面を同期再描画して残像を防ぐ。
        Invalidate(ClientRectangle);
        Update();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var frame = Receiver?.Frame;
        if (frame == null)
        {
            var textArea = Rectangle.Inflate(ClientRectangle, -32, -32);
            TextRenderer.DrawText(e.Graphics, IdleText, Font, textArea, Color.Silver,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }
        double scale = Math.Min((double)ClientSize.Width / frame.Width, (double)ClientSize.Height / frame.Height);
        int w = (int)(frame.Width * scale), h = (int)(frame.Height * scale);
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        e.Graphics.DrawImage(frame, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }
}

/// <summary>外部ソフトから取り込める、映像だけを表示する出力画面。</summary>
internal sealed class VideoOutputForm : Form
{
    private readonly ViewPanel _view = new();

    public VideoOutputForm(FrameReceiver receiver)
    {
        Text = "OpenMeow 映像出力";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        TopMost = false;
        BackColor = Color.Black;
        MinimumSize = new Size(640, 360);

        var workingArea = Screen.PrimaryScreen!.WorkingArea;
        int width = Math.Min(1280, Math.Max(640, workingArea.Width - 64));
        int height = width * 9 / 16;
        if (height > workingArea.Height - 64)
        {
            height = Math.Max(360, workingArea.Height - 64);
            width = height * 16 / 9;
        }
        ClientSize = new Size(width, height);

        _view.Dock = DockStyle.Fill;
        _view.Receiver = receiver;
        _view.IdleText = "SteamVR の映像を待っています…";
        Controls.Add(_view);

        // TopMost のメイン画面から開いても背面に隠さず、ゲームへ戻った後は
        // 通常ウィンドウへ戻してプレイ画面を覆わないようにする。
        Activated += (_, _) =>
        {
            TopMost = true;
            BringToFront();
        };
        Deactivate += (_, _) => TopMost = false;
    }

    public void RefreshFrame() => _view.Invalidate();
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
    private readonly Button _outputBtn;
    private readonly Button _settingsBtn;
    private VideoOutputForm? _outputForm;

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
        Font = new Font("Yu Gothic UI", 10f);
        MinimumSize = new Size(620, 460);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Size = new Size(Math.Min(960, wa.Width - 32), Math.Min(760, wa.Height - 32));
        Location = new Point(Math.Max(wa.Left + 16, wa.Right - Width - 16),
            Math.Max(wa.Top + 16, wa.Bottom - Height - 16));

        // ── ヘッダ(状態表示 + 起動/説明トグルボタン)──
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Color.FromArgb(18, 20, 26) };

        _launchBtn = MakeButton("SteamVR を起動", Color.FromArgb(59, 130, 246));
        _launchBtn.Click += (_, _) => LaunchSteamVr();
        _helpBtn = MakeButton("操作説明を隠す", Color.FromArgb(55, 58, 68));
        _helpBtn.Click += (_, _) => ToggleHelp();
        _outputBtn = MakeButton("映像出力", Color.FromArgb(109, 76, 170));
        _outputBtn.Click += (_, _) => ShowVideoOutput();
        _settingsBtn = MakeButton("設定", Color.FromArgb(75, 78, 92));
        _settingsBtn.Click += (_, _) => ShowSettings();

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 10, 8, 0),
            BackColor = Color.Transparent,
        };
        bar.Controls.Add(_launchBtn);
        bar.Controls.Add(_helpBtn);
        bar.Controls.Add(_outputBtn);
        bar.Controls.Add(_settingsBtn);

        _state.Dock = DockStyle.Fill;
        _state.Font = new Font("Yu Gothic UI", 12f, FontStyle.Bold);
        _state.ForeColor = Color.Orange;
        _state.TextAlign = ContentAlignment.MiddleLeft;
        _state.Padding = new Padding(12, 0, 0, 0);

        header.Controls.Add(_state);
        header.Controls.Add(bar);

        // ── 操作説明(2カラムに整理)──
        _help.Dock = DockStyle.Bottom;
        _help.Height = 216;
        _help.BackColor = Color.FromArgb(20, 22, 28);
        _help.Padding = new Padding(2, 6, 2, 6);
        var helpHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var grid = new TableLayoutPanel
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        var leftHelp = MakeHelpLabel(
            "◆ 視点・トリガー\n" +
            "マウス移動 … 見回し(手は視線へ自動照準)\n" +
            "左クリック … トリガー / 右クリック … グリップ\n" +
            "\n" +
            "◆ 手の操作(押している間だけ有効)\n" +
            "左Shift+マウス … 右腕の位置 / +中ドラッグ … 腕傾け\n" +
            "左Ctrl+マウス … 左腕の位置 / +中ドラッグ … 腕傾け\n" +
            "横ドラッグ … 腕を横倒し / 縦 … 腕を上下\n" +
            "中ドラッグ単独 … 右腕 / X1・X2 … 左腕の直接操作\n" +
            "Tab … 左パッド(歩行) / R … 右パッド(旋回)\n" +
            "※ パッド系 +左クリックで押し込み");
        var rightHelp = MakeHelpLabel(
            "◆ 移動・視点\n" +
            "WASD … 移動 / Q・E … 下降・上昇\n" +
            "矢印 … 頭の微回転\n" +
            "右Shift … 高速 / 右Ctrl … 低速\n" +
            "BackSpace … リセット / ESC … 操作解除\n" +
            "\n" +
            "◆ ボタン・トグル\n" +
            "Y・B … 右手・左手グリップ保持\n" +
            "F5・F6 … 左右・上下 反転\n" +
            "左手 … Z X C V + T F G H + F7\n" +
            "右手 … U O P M + I J K L + F8");
        grid.Controls.Add(leftHelp, 0, 0);
        grid.Controls.Add(rightHelp, 1, 0);
        CenterContent(grid, helpHost, 1200);

        void LayoutHelp()
        {
            CenterContent(grid, helpHost, 1200);
            int columnWidth = Math.Max(1, grid.ClientSize.Width / 2 - 16);
            int contentHeight = Math.Max(
                leftHelp.GetPreferredSize(new Size(columnWidth, 0)).Height,
                rightHelp.GetPreferredSize(new Size(columnWidth, 0)).Height);
            int requiredHeight = Math.Max(240, contentHeight + _help.Padding.Vertical + 12);
            if (_help.Height != requiredHeight) _help.Height = requiredHeight;
        }

        helpHost.Resize += (_, _) => LayoutHelp();
        helpHost.Controls.Add(grid);
        _help.Controls.Add(helpHost);
        LayoutHelp();

        _viewPanel.Dock = DockStyle.Fill;
        _viewPanel.Receiver = _receiver;
        _viewPanel.Cursor = Cursors.Cross;

        Controls.Add(_viewPanel);
        Controls.Add(_help);
        Controls.Add(header);

        _viewPanel.MouseDown += (_, e) => { if (!_captured && e.Button == MouseButtons.Left) StartCapture(); };
        _viewPanel.MouseWheel += (_, e) => { if (_captured) _channel.Wheel += e.Delta / 120.0; };
        MouseWheel += (_, e) => { if (_captured) _channel.Wheel += e.Delta / 120.0; };
        FormClosed += (_, _) =>
        {
            _outputForm?.Close();
            ReleaseCapture();
            _channel.Dispose();
            _receiver.Dispose();
        };
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
            Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = accent,
            AutoSize = true,
            Height = 36,
            Margin = new Padding(6, 0, 0, 0),
            Padding = new Padding(14, 5, 14, 5),
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
        Font = new Font("Yu Gothic UI", 10.5f),
        ForeColor = Color.Gainsboro,
        Padding = new Padding(10, 2, 6, 2),
    };

    private static void CenterContent(Control content, Control host, int maximumWidth)
    {
        int width = Math.Min(maximumWidth, Math.Max(0, host.ClientSize.Width));
        content.SetBounds((host.ClientSize.Width - width) / 2, 0, width, host.ClientSize.Height);
    }

    private void ToggleHelp()
    {
        _help.Visible = !_help.Visible;
        _helpBtn.Text = _help.Visible ? "操作説明を隠す" : "操作説明を表示";
    }

    private void ShowVideoOutput()
    {
        if (_outputForm is { IsDisposed: false })
        {
            if (_outputForm.WindowState == FormWindowState.Minimized)
                _outputForm.WindowState = FormWindowState.Normal;
            _outputForm.Activate();
            _outputForm.BringToFront();
            return;
        }

        var output = new VideoOutputForm(_receiver);
        output.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_outputForm, output)) _outputForm = null;
        };
        _outputForm = output;

        // 所有ウィンドウにはしない。メイン画面を最小化しても映像出力を残すため。
        output.Show();
        output.Activate();
    }

    private void ShowSettings()
    {
        using var settings = new SettingsForm
        {
            // TopMost のメイン画面より背面に回らないよう、同じ前面レベルを引き継ぐ。
            TopMost = TopMost,
        };
        settings.Shown += (_, _) =>
        {
            settings.Activate();
            settings.BringToFront();
        };
        settings.ShowDialog(this);
        if (settings.UninstallStarted) Close();
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
            ? "映像を待っています…\n映像が届いたらクリックして操作開始"
            : "右上の『SteamVR を起動』を押してください\n起動後、映像が届いたらクリックして操作開始";

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

        if (_receiver.Poll())
        {
            _viewPanel.Invalidate();
            _outputForm?.RefreshFrame();
        }
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

/// <summary>キー割り当てとインストール管理をまとめた設定画面。</summary>
internal sealed class SettingsForm : Form
{
    private readonly KeyBindings _bindings = KeyBindings.LoadOrDefault();
    private readonly DesktopMotionSettings _motion = DesktopMotionSettings.LoadOrDefault();
    private readonly bool _initialBodyTrackers;
    private readonly Dictionary<string, Button> _keyButtons = new(StringComparer.Ordinal);
    private readonly Label _info = new();
    private ComboBox? _presetSelector;
    private Label? _motionHighlights;
    private CheckBox? _bodyTrackersCheck;
    private NumericUpDown? _bodyHeightInput;
    private NumericUpDown? _strideInput;
    private NumericUpDown? _stepHeightInput;
    private NumericUpDown? _footPlantInput;
    private Button? _capturingButton;
    private string? _capturingId;

    public bool UninstallStarted { get; private set; }

    public SettingsForm()
    {
        _initialBodyTrackers = _motion.EnableBodyTrackers;
        Text = "OpenMeow 設定";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 560);
        Size = new Size(820, 680);
        BackColor = Color.FromArgb(24, 26, 32);
        ForeColor = Color.Gainsboro;
        Font = new Font("Yu Gothic UI", 10f);
        KeyPreview = true;
        KeyDown += CaptureKey;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 5) };
        tabs.TabPages.Add(CreateMotionTab());
        foreach (string group in KeyBindings.Definitions.Select(x => x.Group).Distinct())
            tabs.TabPages.Add(CreateTab(group));

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            Text = "キーまたは操作感を変更して保存してください。ドライバは約1秒ごとに設定を確認して反映します。",
            ForeColor = Color.LightGray,
            Padding = new Padding(12, 9, 12, 0),
        };
        _info.Dock = DockStyle.Top;
        _info.Height = 24;
        _info.Padding = new Padding(12, 2, 12, 0);
        _info.ForeColor = Color.Khaki;

        var save = MakeActionButton("保存", Color.FromArgb(34, 150, 90));
        save.Click += (_, _) => SaveAndClose();
        var defaults = MakeActionButton("初期設定に戻す", Color.FromArgb(75, 78, 92));
        defaults.Click += (_, _) => ResetDefaults();
        var uninstall = MakeActionButton("ドライバ登録を削除…", Color.FromArgb(170, 55, 55));
        uninstall.Click += (_, _) => UninstallDriver();
        var cancel = MakeActionButton("キャンセル", Color.FromArgb(55, 58, 68));
        cancel.Click += (_, _) => Close();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 8, 12, 8),
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        actions.Controls.Add(defaults);
        actions.Controls.Add(uninstall);

        Controls.Add(tabs);
        Controls.Add(_info);
        Controls.Add(note);
        Controls.Add(actions);
        UpdateConflictInfo();
    }

    private TabPage CreateMotionTab()
    {
        var tab = new TabPage("操作感") { BackColor = Color.FromArgb(24, 26, 32), ForeColor = Color.Gainsboro };
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18) };
        var title = new Label
        {
            Text = "操作感プロファイル",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(20, 20),
        };
        var description = new Label
        {
            Text = "移動速度・マウス感度・手の追従をまとめて切り替えます。\nLegacy は従来の挙動です。その他は実験的な二次系スムージングを使います。",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = Color.LightGray,
            Location = new Point(20, 58),
        };
        var selector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            Location = new Point(20, 125),
            BackColor = Color.FromArgb(55, 58, 68),
            ForeColor = Color.White,
        };
        selector.Items.AddRange(DesktopMotionSettings.PresetNames.Cast<object>().ToArray());
        selector.SelectedItem = _motion.Preset;
        selector.SelectedIndexChanged += (_, _) =>
        {
            if (selector.SelectedItem is string name)
            {
                _motion.ApplyPreset(name);
                RefreshGaitEditors();
                UpdateMotionHighlights();
            }
        };
        _presetSelector = selector;
        _motionHighlights = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = Color.Khaki,
            Location = new Point(20, 175),
        };
        var bodyCheck = new CheckBox
        {
            Text = "腰・両足トラッカー",
            AutoSize = true,
            Checked = _motion.EnableBodyTrackers,
            ForeColor = Color.White,
            Location = new Point(20, 275),
        };
        bodyCheck.CheckedChanged += (_, _) =>
        {
            _motion.EnableBodyTrackers = bodyCheck.Checked;
            UpdateMotionHighlights();
        };
        _bodyTrackersCheck = bodyCheck;

        var restartNote = new Label
        {
            Text = "SteamVRを再起動すると反映（腰 / Left Foot / Right Foot はManage Trackersで手動割当）",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = Color.LightGray,
            Location = new Point(20, 305),
        };
        var gaitControls = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Location = new Point(20, 335),
            MaximumSize = new Size(700, 72),
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        _bodyHeightInput = AddGaitEditor(gaitControls, "身長(m)", 1.2m, 2.2m, 0.01m, (decimal)_motion.BodyHeightMeters);
        _strideInput = AddGaitEditor(gaitControls, "歩幅(m)", 0.1m, 1.2m, 0.01m, (decimal)_motion.StrideLengthMeters);
        _stepHeightInput = AddGaitEditor(gaitControls, "足上げ(m)", 0m, 0.3m, 0.01m, (decimal)_motion.StepHeightMeters);
        _footPlantInput = AddGaitEditor(gaitControls, "接地(0-1)", 0m, 1m, 0.01m, (decimal)_motion.FootPlantStrength);
        _bodyHeightInput.ValueChanged += (_, _) => _motion.BodyHeightMeters = (double)_bodyHeightInput.Value;
        _strideInput.ValueChanged += (_, _) => _motion.StrideLengthMeters = (double)_strideInput.Value;
        _stepHeightInput.ValueChanged += (_, _) => _motion.StepHeightMeters = (double)_stepHeightInput.Value;
        _footPlantInput.ValueChanged += (_, _) => _motion.FootPlantStrength = (double)_footPlantInput.Value;
        panel.Controls.Add(title);
        panel.Controls.Add(description);
        panel.Controls.Add(selector);
        panel.Controls.Add(_motionHighlights);
        panel.Controls.Add(bodyCheck);
        panel.Controls.Add(restartNote);
        panel.Controls.Add(gaitControls);
        tab.Controls.Add(panel);
        UpdateMotionHighlights();
        return tab;
    }

    private void UpdateMotionHighlights()
    {
        if (_motionHighlights is null) return;
        _motionHighlights.Text =
            $"選択中: {_motion.Preset}\n" +
            $"移動 {_motion.MovementSpeed:0.##} m/s   高速×{_motion.FastMultiplier:0.##}   低速×{_motion.SlowMultiplier:0.##}\n" +
            $"旋回 {_motion.TurnDegreesPerSecond:0.#}°/s   視点感度 {_motion.MouseSensitivityDegreesPerPixel:0.###}°/px\n" +
            $"手: {_motion.HandSmoothingMode} / {_motion.HandSpringHz:0.#} Hz / 減衰 {_motion.HandDamping:0.##} / 予測 {_motion.HandPredictionSeconds:0.###} s\n" +
            $"身体: {(_motion.EnableBodyTrackers ? "有効" : "無効")} / 身長 {_motion.BodyHeightMeters:0.##} m / 歩幅 {_motion.StrideLengthMeters:0.##} m / 足上げ {_motion.StepHeightMeters:0.##} m / 接地 {_motion.FootPlantStrength:P0}";
    }

    private static NumericUpDown AddGaitEditor(FlowLayoutPanel panel, string label, decimal min,
        decimal max, decimal increment, decimal value)
    {
        var box = new Panel { Width = 132, Height = 52, Margin = new Padding(0, 0, 6, 0) };
        var text = new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Location = new Point(0, 0),
        };
        var input = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = increment,
            DecimalPlaces = 2,
            Value = Math.Clamp(value, min, max),
            Width = 92,
            Location = new Point(0, 22),
        };
        box.Controls.Add(text);
        box.Controls.Add(input);
        panel.Controls.Add(box);
        return input;
    }

    private void RefreshGaitEditors()
    {
        if (_bodyHeightInput is null || _strideInput is null || _stepHeightInput is null || _footPlantInput is null) return;
        _bodyHeightInput.Value = Math.Clamp((decimal)_motion.BodyHeightMeters, _bodyHeightInput.Minimum, _bodyHeightInput.Maximum);
        _strideInput.Value = Math.Clamp((decimal)_motion.StrideLengthMeters, _strideInput.Minimum, _strideInput.Maximum);
        _stepHeightInput.Value = Math.Clamp((decimal)_motion.StepHeightMeters, _stepHeightInput.Minimum, _stepHeightInput.Maximum);
        _footPlantInput.Value = Math.Clamp((decimal)_motion.FootPlantStrength, _footPlantInput.Minimum, _footPlantInput.Maximum);
    }

    private TabPage CreateTab(string group)
    {
        var tab = new TabPage(group) { BackColor = Color.FromArgb(24, 26, 32), ForeColor = Color.Gainsboro };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
            BackColor = Color.FromArgb(30, 32, 40),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30f));

        foreach (var definition in KeyBindings.Definitions.Where(x => x.Group == group))
        {
            var label = new Label
            {
                Text = definition.Label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                ForeColor = Color.Gainsboro,
            };
            var key = new Button
            {
                Text = KeyName(_bindings.Get(definition.Id)),
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 58, 68),
                ForeColor = Color.White,
                Tag = definition.Id,
                Height = 30,
                Margin = new Padding(4),
                TabStop = false,
            };
            key.FlatAppearance.BorderColor = Color.FromArgb(85, 88, 100);
            key.Click += BeginCapture;
            _keyButtons[definition.Id] = key;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            table.Controls.Add(label);
            table.Controls.Add(key);
        }

        void LayoutTable()
        {
            int availableWidth = Math.Max(0, scroll.ClientSize.Width - scroll.Padding.Horizontal);
            int width = Math.Min(960, availableWidth);
            table.Width = width;
            table.Left = scroll.Padding.Left + (availableWidth - width) / 2;
            table.Top = scroll.Padding.Top;
        }

        LayoutTable();
        scroll.Resize += (_, _) => LayoutTable();
        scroll.Controls.Add(table);
        tab.Controls.Add(scroll);
        return tab;
    }

    private static Button MakeActionButton(string text, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        Height = 30,
        FlatStyle = FlatStyle.Flat,
        BackColor = color,
        ForeColor = Color.White,
        Margin = new Padding(4, 0, 0, 0),
        Padding = new Padding(10, 3, 10, 3),
        UseVisualStyleBackColor = false,
    };

    private static string KeyName(int key)
    {
        if (key == 0) return "なし";
        return new KeysConverter().ConvertToString((Keys)key) ?? $"VK 0x{key:X2}";
    }

    private void BeginCapture(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        _capturingButton = button;
        _capturingId = id;
        button.Text = "キーを押してください…";
        button.BackColor = Color.FromArgb(180, 120, 35);
        button.Focus();
        _info.Text = "Escでキャンセルできます。";
    }

    private void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (_capturingId == null || _capturingButton == null) return;
        if (e.KeyCode == Keys.Escape)
        {
            _capturingId = null;
            _capturingButton.BackColor = Color.FromArgb(55, 58, 68);
            UpdateAllKeyButtons();
        }
        else
        {
            _bindings.Set(_capturingId, (int)e.KeyCode);
            _capturingId = null;
            _capturingButton.BackColor = Color.FromArgb(55, 58, 68);
            UpdateAllKeyButtons();
            UpdateConflictInfo();
        }
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    private void UpdateAllKeyButtons()
    {
        foreach (var definition in KeyBindings.Definitions)
            if (_keyButtons.TryGetValue(definition.Id, out var button))
                button.Text = KeyName(_bindings.Get(definition.Id));
        if (_capturingButton != null) _capturingButton.BackColor = Color.FromArgb(55, 58, 68);
    }

    private void UpdateConflictInfo()
    {
        var conflicts = KeyBindings.Definitions
            .Where(x => _bindings.Get(x.Id) != 0)
            .GroupBy(x => _bindings.Get(x.Id))
            .Where(x => x.Count() > 1)
            .Select(x => KeyName(x.Key))
            .ToArray();
        _info.Text = conflicts.Length == 0
            ? ""
            : "注意: 重複しているキーがあります（同時使用時は両方の操作が反応します）: " + string.Join(", ", conflicts);
    }

    private void ResetDefaults()
    {
        if (MessageBox.Show(this, "キー割り当てと操作感を初期設定に戻しますか?", "確認",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var defaults = KeyBindings.Defaults();
        foreach (var definition in KeyBindings.Definitions) _bindings.Set(definition.Id, defaults.Get(definition.Id));
        _motion.ApplyPreset("Legacy");
        _motion.EnableBodyTrackers = false;
        if (_bodyTrackersCheck is not null) _bodyTrackersCheck.Checked = false;
        RefreshGaitEditors();
        if (_presetSelector is not null) _presetSelector.SelectedItem = "Legacy";
        UpdateMotionHighlights();
        UpdateAllKeyButtons();
        UpdateConflictInfo();
    }

    private void SaveAndClose()
    {
        try
        {
            _bindings.Save();
            _motion.Save();
            bool bodyTopologyChanged = _motion.EnableBodyTrackers != _initialBodyTrackers;
            if (bodyTopologyChanged)
            {
                MessageBox.Show(this,
                    "腰・両足トラッカーの構成を変更しました。SteamVRを完全に終了して再起動すると反映されます。",
                    "SteamVRの再起動が必要です", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "設定を保存できませんでした。\n\n" + ex.Message,
                "OpenMeow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UninstallDriver()
    {
        string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "uninstall.ps1"));
        if (!File.Exists(script))
        {
            MessageBox.Show(this, "アンインストール用スクリプトが見つかりません。install.ps1を再実行してください。",
                "OpenMeow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this,
                "SteamVRへのOpenMeowドライバ登録とスタートメニュー登録を削除します。続行しますか?\n\nSteamVRは終了してから実行してください。",
                "ドライバ登録を削除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(script)!,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-NoPause");
            Process.Start(start);
            UninstallStarted = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "アンインストールを開始できませんでした。\n\n" + ex.Message,
                "OpenMeow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
