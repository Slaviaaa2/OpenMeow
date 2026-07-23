using System.Drawing.Drawing2D;
using OpenMeow.Lab.Domain;
using OpenMeow.Lab.Orchestration;

namespace OpenMeow.Lab.UI;

/// <summary>
/// A small, deliberately dependency-free control tower for the Lab simulator.
/// It owns no simulation state: each frame is painted from the immutable snapshots
/// returned by <see cref="ControlTower"/>.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color Surface = Color.FromArgb(20, 23, 31);
    private static readonly Color SurfaceRaised = Color.FromArgb(28, 32, 43);
    private static readonly Color SurfaceInset = Color.FromArgb(15, 18, 25);
    private static readonly Color Border = Color.FromArgb(51, 58, 76);
    private static readonly Color TextPrimary = Color.FromArgb(236, 241, 250);
    private static readonly Color TextMuted = Color.FromArgb(155, 166, 187);
    private static readonly Color Accent = Color.FromArgb(91, 213, 190);
    private static readonly Color AccentBlue = Color.FromArgb(103, 164, 255);
    private static readonly Color AccentPink = Color.FromArgb(236, 122, 196);

    private readonly ControlTower _tower;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, WorldSnapshot> _snapshots = new();
    private readonly Dictionary<Guid, EvaluationResult> _evaluations = new();
    private readonly List<Station> _stations = new();

    private readonly WorldCanvas _canvas;
    private readonly ListBox _stationList;
    private readonly Label _selectedTitle;
    private readonly Label _selectedDescription;
    private readonly Label _statusLabel;
    private readonly Label _metricLabel;
    private readonly DataGridView _profileGrid;
    private readonly Button _benchmarkButton;
    private readonly Button _gaitBenchmarkButton;
    private readonly Button _resetButton;
    private readonly Button _tuneButton;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Font _stationIdFont = new("Segoe UI", 7.5f);
    private readonly Font _stationScoreFont = new("Segoe UI Semibold", 9f, FontStyle.Bold);
    private Label _gaitLabel = null!;
    private GaitBenchmarkResult? _latestGait;

    private Guid _selectedId;
    private Task? _operationTask;
    private CancellationTokenSource? _operationCancellation;
    private DateTime _lastDrag = DateTime.MinValue;
    private bool _disposed;

    public MainForm(ControlTower? tower = null)
    {
        _tower = tower ?? new ControlTower();

        Text = "OpenMeow Lab · Research Harness";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1060, 680);
        ClientSize = new Size(1360, 840);
        BackColor = SurfaceInset;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;

        // One station is intentionally created for every catalog task. Keeping the
        // ids stable for the lifetime of this window makes selection and dragging
        // independent of timer redraws.
        foreach (ResearchTaskDefinition task in _tower.Tasks)
        {
            WorldSnapshot snapshot = _tower.Create(new CreateExperimentRequest
            {
                AgentId = $"tower-{_stations.Count + 1:00}",
                SubjectId = "default_mew",
                TaskId = task.Id,
                Seed = _stations.Count + 1,
            });
            _stations.Add(new Station(snapshot.ExperimentId, task));
            _snapshots[snapshot.ExperimentId] = snapshot;
            _evaluations[snapshot.ExperimentId] = _tower.Evaluate(snapshot.ExperimentId);
        }

        _selectedId = _stations.Count > 0 ? _stations[0].Id : Guid.Empty;

        var header = BuildHeader(out _statusLabel, out _benchmarkButton, out _gaitBenchmarkButton, out _resetButton, out _tuneButton);
        _stationList = BuildStationList();
        _selectedTitle = new Label();
        _selectedDescription = new Label();
        _metricLabel = new Label();
        _profileGrid = BuildProfileGrid();
        _gaitLabel = new Label();
        var details = BuildDetailsPanel(_selectedTitle, _selectedDescription, _metricLabel, _profileGrid, _gaitLabel);

        _canvas = new WorldCanvas
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = SurfaceInset,
        };
        _canvas.DragTargetRequested += CanvasOnDragTargetRequested;

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceInset,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(12, 10, 12, 12),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 238));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 326));
        body.Controls.Add(BuildStationPane(_stationList), 0, 0);
        body.Controls.Add(_canvas, 1, 0);
        body.Controls.Add(details, 2, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceInset,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);
        Controls.Add(root);

        _stationList.SelectedIndexChanged += (_, _) => SelectStation();
        _benchmarkButton.Click += (_, _) => StartOperation("Running benchmark…", RunBenchmarkAsync);
        _gaitBenchmarkButton.Click += (_, _) => StartOperation("Running FULL BODY GAIT benchmark…", RunGaitBenchmarkAsync);
        _resetButton.Click += (_, _) => StartOperation("Resetting station…", ResetAsync);
        _tuneButton.Click += (_, _) => StartOperation("Auto-tuning profile…", AutoTuneAsync);

        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += (_, _) => RefreshSnapshots();
        _timer.Start();
        FormClosed += (_, _) => DisposeTowerUi();

        if (_stationList.Items.Count > 0)
            _stationList.SelectedIndex = 0;
        RefreshSnapshots();
    }

    private Panel BuildHeader(out Label status, out Button benchmark, out Button gaitBenchmark, out Button reset, out Button tune)
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        var title = new Label
        {
            Dock = DockStyle.Left,
            AutoSize = false,
            Width = 360,
            Text = "  OPENMEOW LAB  /  RESEARCH HARNESS",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
            ForeColor = TextPrimary,
        };
        status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9f),
            ForeColor = TextMuted,
            Text = "Booting research stations…",
            Padding = new Padding(10, 0, 0, 0),
        };

        benchmark = MakeButton("Run benchmark", AccentBlue);
        gaitBenchmark = MakeButton("FULL BODY GAIT", AccentPink);
        reset = MakeButton("Reset", Color.FromArgb(73, 81, 104));
        tune = MakeButton("Auto-tune", Accent);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 14, 12, 0),
            BackColor = Color.Transparent,
        };
        actions.Controls.Add(benchmark);
        actions.Controls.Add(gaitBenchmark);
        actions.Controls.Add(reset);
        actions.Controls.Add(tune);

        header.Controls.Add(actions);
        header.Controls.Add(status);
        header.Controls.Add(title);
        return header;
    }

    private ListBox BuildStationList()
    {
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = SurfaceRaised,
            ForeColor = TextPrimary,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 72,
            IntegralHeight = false,
            FormattingEnabled = true,
        };
        foreach (Station station in _stations)
            list.Items.Add(station);
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= _stations.Count) return;
            Station station = _stations[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(selected ? Color.FromArgb(46, 67, 82) : SurfaceRaised);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var line = new Pen(selected ? Accent : Border, selected ? 2 : 1);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            using var dot = new SolidBrush(TaskColor(station.Task.Id));
            e.Graphics.FillEllipse(dot, e.Bounds.Left + 13, e.Bounds.Top + 17, 12, 12);
            TextRenderer.DrawText(e.Graphics, station.Task.DisplayName, Font,
                new Rectangle(e.Bounds.Left + 36, e.Bounds.Top + 9, e.Bounds.Width - 46, 25),
                TextPrimary, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string id = station.Task.Id.Replace('_', ' ').ToUpperInvariant();
            TextRenderer.DrawText(e.Graphics, id, _stationIdFont,
                new Rectangle(e.Bounds.Left + 36, e.Bounds.Top + 35, e.Bounds.Width - 46, 17),
                TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (_evaluations.TryGetValue(station.Id, out EvaluationResult? evaluation))
                TextRenderer.DrawText(e.Graphics, $"{evaluation.Score:0.0}", _stationScoreFont,
                    new Rectangle(e.Bounds.Right - 57, e.Bounds.Top + 16, 44, 22),
                    Accent, TextFormatFlags.Right | TextFormatFlags.NoPadding);
        };
        return list;
    }

    private Control BuildStationPane(ListBox stationList)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = SurfaceRaised, Padding = new Padding(1) };
        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            Text = "  RESEARCH STATIONS",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = TextMuted,
            BackColor = Surface,
        };
        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Text = "Select a station to inspect\nDrag inside a card to move the right hand",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMuted,
            Padding = new Padding(12, 0, 6, 0),
            BackColor = Surface,
        };
        panel.Controls.Add(stationList);
        panel.Controls.Add(footer);
        panel.Controls.Add(title);
        return panel;
    }

    private Panel BuildDetailsPanel(Label title, Label description, Label metrics, DataGridView profileGrid, Label gaitLabel)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = SurfaceRaised, Padding = new Padding(16, 14, 16, 12), AutoScroll = true };
        title.Dock = DockStyle.Top;
        title.Height = 32;
        title.Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
        title.ForeColor = TextPrimary;
        description.Dock = DockStyle.Top;
        description.Height = 66;
        description.Font = new Font("Segoe UI", 9f);
        description.ForeColor = TextMuted;
        description.Padding = new Padding(0, 2, 0, 8);

        var metricsHeader = MakeSectionLabel("LIVE METRICS");
        metrics.Dock = DockStyle.Top;
        metrics.Height = 126;
        metrics.Font = new Font("Consolas", 9f);
        metrics.ForeColor = TextPrimary;
        metrics.Padding = new Padding(0, 3, 0, 8);

        var profileHeader = MakeSectionLabel("MOTION PROFILE");
        profileGrid.Dock = DockStyle.Top;
        profileGrid.Height = 184;

        var gaitHeader = MakeSectionLabel("FULL BODY GAIT · LATEST RESEARCH");
        gaitLabel.Dock = DockStyle.Top;
        gaitLabel.Height = 108;
        gaitLabel.Font = new Font("Consolas", 8.5f);
        gaitLabel.ForeColor = TextPrimary;
        gaitLabel.BackColor = SurfaceInset;
        gaitLabel.Padding = new Padding(9, 8, 6, 6);
        gaitLabel.Text = "No gait benchmark yet.\nUse FULL BODY GAIT to run the 90 Hz deterministic scenario.";

        var hintHeader = MakeSectionLabel("API / MCP USAGE");
        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 132,
            Text = "Create → Observe → Act\n\n" +
                   "tower.Create(new CreateExperimentRequest {\n" +
                   "  TaskId = \"head_petting\" });\n" +
                   "tower.Act(id, new ActionRequest {\n" +
                   "  Target = new Vec3(x, y, z) });\n\n" +
                   "Works from API, MCP or this tower.",
            Font = new Font("Consolas", 8.5f),
            ForeColor = Color.FromArgb(170, 190, 211),
            BackColor = SurfaceInset,
            Padding = new Padding(9, 8, 6, 6),
        };

        panel.Controls.Add(hint);
        panel.Controls.Add(hintHeader);
        panel.Controls.Add(gaitLabel);
        panel.Controls.Add(gaitHeader);
        panel.Controls.Add(profileGrid);
        panel.Controls.Add(profileHeader);
        panel.Controls.Add(metrics);
        panel.Controls.Add(metricsHeader);
        panel.Controls.Add(description);
        panel.Controls.Add(title);
        return panel;
    }

    private DataGridView BuildProfileGrid()
    {
        var grid = new DataGridView
        {
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            ColumnHeadersVisible = false,
            BorderStyle = BorderStyle.None,
            BackgroundColor = SurfaceInset,
            GridColor = Border,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EnableHeadersVisualStyles = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Parameter", Width = 146, FillWeight = 45, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = TextMuted, BackColor = SurfaceInset } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = TextPrimary, BackColor = SurfaceInset, Alignment = DataGridViewContentAlignment.MiddleRight } });
        return grid;
    }

    private static Label MakeSectionLabel(string text) => new()
    {
        Dock = DockStyle.Top,
        Height = 26,
        Text = text,
        Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
        ForeColor = Accent,
        Padding = new Padding(0, 5, 0, 0),
    };

    private static Button MakeButton(string text, Color color)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = color,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            Padding = new Padding(11, 3, 11, 3),
            Margin = new Padding(5, 0, 0, 0),
            UseVisualStyleBackColor = false,
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, .12f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(color, .12f);
        return button;
    }

    private static Color TaskColor(string taskId) => taskId.ToLowerInvariant() switch
    {
        "head_petting" => Color.FromArgb(106, 195, 255),
        "cheek_nuzzle" => Color.FromArgb(237, 122, 198),
        "limp_support" => Color.FromArgb(255, 181, 82),
        "hand_hold" => Color.FromArgb(125, 218, 169),
        _ => Color.FromArgb(147, 162, 255),
    };

    private void SelectStation()
    {
        if (_stationList.SelectedItem is not Station station) return;
        _selectedId = station.Id;
        UpdateDetails();
        _canvas.SelectedExperimentId = _selectedId;
        _canvas.Invalidate();
    }

    private void RefreshSnapshots()
    {
        if (_disposed) return;
        try
        {
            foreach (Station station in _stations)
            {
                WorldSnapshot snapshot = _tower.Observe(station.Id);
                _snapshots[station.Id] = snapshot;
                _evaluations[station.Id] = _tower.Evaluate(station.Id);
            }
            _canvas.SetStations(_stations.Select(station => new StationRenderData(
                station.Id,
                station.Task,
                _snapshots[station.Id],
                _evaluations[station.Id],
                GetParentMap(station.Task, _snapshots[station.Id].SubjectId))).ToArray(), _selectedId);
            _stationList.Invalidate();
            UpdateDetails();
            if (_operationTask is null or { IsCompleted: true })
                _statusLabel.Text = $"{_stations.Count} stations online  ·  tick {_snapshots.Values.Sum(s => s.Tick):N0}";
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Station read error: {ex.Message}";
        }
    }

    private Dictionary<string, string?> GetParentMap(ResearchTaskDefinition task, string subjectId)
    {
        try
        {
            return _tower.Subjects.Get(subjectId).Parts.ToDictionary(part => part.Id, part => part.Parent, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void UpdateDetails()
    {
        if (_disposed || !_snapshots.TryGetValue(_selectedId, out WorldSnapshot? snapshot)) return;
        Station station = _stations.FirstOrDefault(item => item.Id == _selectedId) ?? _stations[0];
        EvaluationResult evaluation = _evaluations[_selectedId];
        _selectedTitle.Text = station.Task.DisplayName;
        _selectedDescription.Text = station.Task.Description;
        MetricSnapshot m = snapshot.Metrics;
        string contact = m.ContactSeconds <= 0 ? "idle" : $"{m.ContactSeconds:0.00}s contact";
        string settling = m.PostContactSampledSeconds <= 0
            ? "not measured"
            : $"{m.PostContactRmsSubjectSpeed:0.000}m/s rms";
        _metricLabel.Text = $"SCORE       {evaluation.Score,6:0.0}\n" +
                            $"STATUS      {(snapshot.Contacts.Count == 0 ? "READY" : "CONTACT")}  ·  {contact}\n" +
                            $"TARGET F    {m.MeanTargetForce:0.00} mean / {m.PeakTargetForce:0.00} peak\n" +
                            $"TARGET V    {m.MeanTargetContactSpeed:0.000}m/s   DIR {m.DirectionReversals}\n" +
                            $"SETTLING    {settling}\n" +
                            $"TRAVEL      {m.StrokeTravel:0.000}m   REV {snapshot.Revision}";
        if (_latestGait is not null)
            _gaitLabel.Text = FormatGaitResearch(_latestGait);

        MotionProfile p = snapshot.Profile;
        _profileGrid.Rows.Clear();
        AddProfileRow("Profile", p.Name);
        AddProfileRow("Spring frequency", $"{p.PositionSpringHz:0.00} Hz");
        AddProfileRow("Damping ratio", $"{p.DampingRatio:0.00}");
        AddProfileRow("Max speed", $"{p.MaxSpeed:0.00} m/s");
        AddProfileRow("Max acceleration", $"{p.MaxAcceleration:0.00} m/s²");
        AddProfileRow("Contact compliance", $"{p.ContactCompliance:0.00}");
        AddProfileRow("Prediction", $"{p.PredictionSeconds * 1000:0} ms");

        void AddProfileRow(string name, string value)
        {
            int index = _profileGrid.Rows.Add(name, value);
            _profileGrid.Rows[index].Height = 23;
            _profileGrid.Rows[index].DefaultCellStyle.SelectionBackColor = SurfaceInset;
            _profileGrid.Rows[index].DefaultCellStyle.SelectionForeColor = TextPrimary;
        }
    }

    private static string FormatGaitResearch(GaitBenchmarkResult result)
    {
        GaitMetrics m = result.Metrics;
        return $"SCORE       {result.Score,6:0.0}\n" +
               $"PROFILE     H {result.Profile.BodyHeightMeters:0.00}m  stride {result.Profile.StrideLengthMeters:0.00}m\n" +
               $"PLANT       slip {m.PlantedFootWorldSlipMetersPerSecond:0.000}m/s  height {m.PlantedHeightErrorMeters:0.000}m\n" +
               $"SWING       clearance {m.SwingClearanceMeters:0.000}m  steps {m.SwingSteps}\n" +
               $"TURN/STOP   toe {m.ToeTurnAlignmentDegrees:0.0}°  settle {m.StopSettlingSpeed:0.000}m/s";
    }

    private void StartOperation(string status, Func<CancellationToken, Task> operation)
    {
        if (_disposed || _operationTask is { IsCompleted: false }) return;
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _operationCancellation.Token;
        _statusLabel.Text = status;
        SetActionButtons(false);
        _operationTask = ExecuteOperationAsync(operation, token);
    }

    private async Task ExecuteOperationAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        try
        {
            await operation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            PostUi(() => _statusLabel.Text = $"Operation failed: {ex.Message}");
        }
        finally
        {
            PostUi(() =>
            {
                SetActionButtons(true);
                RefreshSnapshots();
            });
        }
    }

    private async Task RunBenchmarkAsync(CancellationToken token)
    {
        Guid id = _selectedId;
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            AutoTuner.RunBenchmark(_tower, id);
            token.ThrowIfCancellationRequested();
        }, token).ConfigureAwait(false);
        PostUi(() => _statusLabel.Text = "Benchmark complete · metrics refreshed");
    }

    private async Task RunGaitBenchmarkAsync(CancellationToken token)
    {
        GaitBenchmarkResult result = await Task.Run(
            () => _tower.RunGaitBenchmark(new GaitBenchmarkRequest { Seed = 17 }), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _latestGait = result;
        PostUi(() =>
        {
            _gaitLabel.Text = FormatGaitResearch(result);
            _statusLabel.Text = $"FULL BODY GAIT complete · score {result.Score:0.0}";
        });
    }

    private async Task ResetAsync(CancellationToken token)
    {
        Guid id = _selectedId;
        long expectedRevision = _tower.Observe(id).Revision;
        WorldSnapshot snapshot = await Task.Run(
            () => _tower.Reset(id, expectedRevision), token).ConfigureAwait(false);
        PostUi(() =>
        {
            _snapshots[id] = snapshot;
            _evaluations[id] = _tower.Evaluate(id);
            _statusLabel.Text = "Station reset to its initial state";
        });
    }

    private async Task AutoTuneAsync(CancellationToken token)
    {
        Guid id = _selectedId;
        WorldSnapshot baseline = _tower.Observe(id);
        TuneResult result = await _tower.AutoTuneAsync(new TuneRequest
        {
            SubjectId = baseline.SubjectId,
            TaskId = baseline.TaskId,
            Seed = (int)(baseline.Tick + baseline.Revision + 17),
            Candidates = 16,
            Parallelism = Math.Min(Environment.ProcessorCount, 4),
            Baseline = baseline.Profile,
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        WorldSnapshot updated = await Task.Run(
            () => _tower.SetProfile(id, result.BestProfile, baseline.Revision), token).ConfigureAwait(false);
        PostUi(() =>
        {
            _snapshots[id] = updated;
            _evaluations[id] = _tower.Evaluate(id);
            _statusLabel.Text = $"Auto-tune complete · {result.CandidatesEvaluated} candidates · best {result.BestScore:0.0}";
        });
    }

    private void SetActionButtons(bool enabled)
    {
        _benchmarkButton.Enabled = enabled;
        _gaitBenchmarkButton.Enabled = enabled;
        _resetButton.Enabled = enabled;
        _tuneButton.Enabled = enabled;
    }

    private void CanvasOnDragTargetRequested(object? sender, WorldDragEventArgs e)
    {
        if (_disposed || e.ExperimentId != _selectedId || _operationTask is { IsCompleted: false }) return;
        if ((DateTime.UtcNow - _lastDrag).TotalMilliseconds < 35) return;
        _lastDrag = DateTime.UtcNow;
        try
        {
            // Act is internally synchronized by ResearchWorld. Deliberately avoid
            // an expected revision here: a redraw and a drag can arrive back-to-back.
            WorldSnapshot snapshot = _tower.Act(e.ExperimentId, new ActionRequest
            {
                Hand = HandSide.Right,
                Target = e.Target,
                DurationSeconds = 0.06,
                Label = "canvas-drag",
            });
            _snapshots[e.ExperimentId] = snapshot;
            _evaluations[e.ExperimentId] = _tower.Evaluate(e.ExperimentId);
            _canvas.Invalidate();
            UpdateDetails();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _statusLabel.Text = $"Hand move ignored: {ex.Message}";
        }
    }

    private void PostUi(Action action)
    {
        if (_disposed || IsDisposed || Disposing) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void DisposeTowerUi()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _stationIdFont.Dispose();
        _stationScoreFont.Dispose();
        _canvas.DragTargetRequested -= CanvasOnDragTargetRequested;
        _canvas.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeTowerUi();
        base.Dispose(disposing);
    }

    private sealed record Station(Guid Id, ResearchTaskDefinition Task)
    {
        public override string ToString() => Task.DisplayName;
    }
}

internal sealed record StationRenderData(
    Guid ExperimentId,
    ResearchTaskDefinition Task,
    WorldSnapshot Snapshot,
    EvaluationResult Evaluation,
    IReadOnlyDictionary<string, string?> Parents);

internal sealed class WorldDragEventArgs(Guid experimentId, Vec3 target, Point location) : EventArgs
{
    public Guid ExperimentId { get; } = experimentId;
    public Vec3 Target { get; } = target;
    public Point Location { get; } = location;
}

/// <summary>GDI+ station renderer and the small interaction surface used by MainForm.</summary>
internal sealed class WorldCanvas : Panel
{
    private static readonly Color TextMuted = Color.FromArgb(155, 166, 187);
    private static readonly Color Accent = Color.FromArgb(91, 213, 190);
    private static readonly Color AccentPink = Color.FromArgb(236, 122, 196);
    private readonly Font _taskFont = new("Segoe UI Semibold", 9f, FontStyle.Bold);
    private readonly Font _smallFont = new("Segoe UI", 7.5f);
    private readonly Font _scoreFont = new("Consolas", 9f, FontStyle.Bold);
    private StationRenderData[] _stations = [];
    private RenderSlot[] _slots = [];
    private Guid _selected;
    private bool _dragging;
    private Guid _dragExperiment;

    public WorldCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Default;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseLeave += (_, _) => { if (!_dragging) Cursor = Cursors.Default; };
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Guid SelectedExperimentId
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public event EventHandler<WorldDragEventArgs>? DragTargetRequested;

    public void SetStations(StationRenderData[] stations, Guid selected)
    {
        _stations = stations;
        _selected = selected;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.Clear(Color.FromArgb(13, 16, 23));
        DrawBackdrop(e.Graphics);
        _slots = BuildSlots();
        foreach (RenderSlot slot in _slots)
            DrawStation(e.Graphics, slot);
        if (_stations.Length == 0)
            TextRenderer.DrawText(e.Graphics, "No research tasks registered", Font, ClientRectangle, Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawBackdrop(Graphics g)
    {
        using var gridPen = new Pen(Color.FromArgb(23, 29, 42), 1);
        for (int x = 0; x < ClientSize.Width; x += 32) g.DrawLine(gridPen, x, 0, x, ClientSize.Height);
        for (int y = 0; y < ClientSize.Height; y += 32) g.DrawLine(gridPen, 0, y, ClientSize.Width, y);
        using var glow = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(16, 43, 50), Color.FromArgb(13, 16, 23), 90f);
        g.FillRectangle(glow, ClientRectangle);
    }

    private RenderSlot[] BuildSlots()
    {
        if (_stations.Length == 0) return [];
        int columns = ClientSize.Width >= 640 ? 2 : 1;
        const int gap = 12;
        int width = Math.Max(1, (ClientSize.Width - gap * (columns + 1)) / columns);
        int rows = (int)Math.Ceiling(_stations.Length / (double)columns);
        int height = Math.Max(1, (ClientSize.Height - gap * (rows + 1)) / rows);
        var slots = new RenderSlot[_stations.Length];
        for (int i = 0; i < _stations.Length; i++)
        {
            int col = i % columns;
            int row = i / columns;
            slots[i] = new RenderSlot(
                _stations[i],
                new Rectangle(gap + col * (width + gap), gap + row * (height + gap), width, height));
        }
        return slots;
    }

    private void DrawStation(Graphics g, RenderSlot slot)
    {
        Rectangle card = slot.Bounds;
        bool selected = slot.Data.ExperimentId == _selected;
        Color accent = TaskColor(slot.Data.Task.Id);
        using var background = new LinearGradientBrush(card,
            selected ? Color.FromArgb(40, 48, 66) : Color.FromArgb(28, 33, 45),
            Color.FromArgb(17, 21, 29), 90f);
        using GraphicsPath path = Rounded(card, 12);
        g.FillPath(background, path);
        using var border = new Pen(selected ? accent : Color.FromArgb(53, 61, 80), selected ? 2.2f : 1.1f);
        g.DrawPath(border, path);
        using var accentBrush = new SolidBrush(accent);
        g.FillRectangle(accentBrush, card.Left + 1, card.Top + 1, Math.Min(72, card.Width - 2), 3);

        TextRenderer.DrawText(g, slot.Data.Task.DisplayName, _taskFont,
            new Rectangle(card.Left + 14, card.Top + 12, card.Width - 112, 22), Color.FromArgb(235, 240, 250),
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        string status = slot.Data.Snapshot.Contacts.Count == 0 ? "READY" : "CONTACT";
        using var statusBrush = new SolidBrush(slot.Data.Snapshot.Contacts.Count == 0 ? Color.FromArgb(120, 140, 160) : Color.FromArgb(255, 181, 82));
        g.FillEllipse(statusBrush, card.Right - 84, card.Top + 16, 7, 7);
        TextRenderer.DrawText(g, status, _smallFont,
            new Rectangle(card.Right - 72, card.Top + 13, 60, 16), TextMuted,
            TextFormatFlags.Right | TextFormatFlags.NoPadding);

        Rectangle scene = new(card.Left + 10, card.Top + 42, card.Width - 20, Math.Max(40, card.Height - 76));
        DrawScene(g, slot.Data, scene, accent);
        MetricSnapshot m = slot.Data.Snapshot.Metrics;
        TextRenderer.DrawText(g, $"SCORE {slot.Data.Evaluation.Score:0.0}", _scoreFont,
            new Rectangle(card.Left + 14, card.Bottom - 28, 120, 18), Color.FromArgb(128, 230, 202),
            TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, $"{m.ContactSeconds:0.00}s contact  ·  rev {slot.Data.Snapshot.Revision}", _smallFont,
            new Rectangle(card.Left + 132, card.Bottom - 26, card.Width - 144, 16), TextMuted,
            TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }

    private void DrawScene(Graphics g, StationRenderData station, Rectangle scene, Color accent)
    {
        float centerX = scene.Left + scene.Width / 2f;
        float floorY = scene.Bottom - 5;
        double scale = Math.Min(scene.Width / 3.35, scene.Height / 2.58);
        float centerY = floorY - (float)(0.05 * scale);
        var points = new Dictionary<string, PointF>(StringComparer.OrdinalIgnoreCase);
        foreach (BodyPartSnapshot part in station.Snapshot.Parts)
            points[part.Id] = Project(part.Position, station.Task.SubjectOffset, centerX, centerY, scale);

        using var floorPen = new Pen(Color.FromArgb(70, 82, 104), 1);
        g.DrawEllipse(floorPen, centerX - (float)(.58 * scale), floorY - 7, (float)(1.16 * scale), 12);
        for (int i = -3; i <= 3; i++)
        {
            float x = centerX + (float)(i * .25 * scale);
            g.DrawLine(floorPen, x, floorY - 1, centerX + i * 13, floorY - 29);
        }

        // The snapshots omit Parent by design; the registry fills the topology in
        // for the renderer so the avatar remains an articulated, readable figure.
        using var bonePen = new Pen(Color.FromArgb(100, 115, 144), 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (BodyPartSnapshot part in station.Snapshot.Parts)
            if (station.Parents.TryGetValue(part.Id, out string? parent) && parent is not null && points.TryGetValue(parent, out PointF parentPoint))
                g.DrawLine(bonePen, points[part.Id], parentPoint);

        BodyPartSnapshot? target = station.Snapshot.Parts.FirstOrDefault(part =>
            part.Id.Equals(station.Task.TargetPart, StringComparison.OrdinalIgnoreCase));
        foreach (BodyPartSnapshot part in station.Snapshot.Parts.OrderBy(part => part.Position.Z))
        {
            PointF point = points[part.Id];
            bool isTarget = target is not null && part.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase);
            float radius = (float)Math.Max(3.5, part.Radius * scale * .55);
            Color partColor = isTarget ? accent : SoftnessColor(part.Softness);
            using var fill = new SolidBrush(Color.FromArgb(220, partColor));
            using var outline = new Pen(isTarget ? Color.White : Color.FromArgb(70, 84, 111), isTarget ? 1.7f : 1f);
            g.FillEllipse(fill, point.X - radius, point.Y - radius, radius * 2, radius * 2);
            g.DrawEllipse(outline, point.X - radius, point.Y - radius, radius * 2, radius * 2);
            if (isTarget)
            {
                using var targetPen = new Pen(Color.FromArgb(150, partColor), 1) { DashStyle = DashStyle.Dot };
                g.DrawEllipse(targetPen, point.X - radius - 5, point.Y - radius - 5, radius * 2 + 10, radius * 2 + 10);
            }
        }

        foreach (HandSnapshot hand in station.Snapshot.Hands)
        {
            PointF point = Project(hand.Position, station.Task.SubjectOffset, centerX, centerY, scale);
            Color handColor = hand.Side == HandSide.Right ? Accent : AccentPink;
            using var handPen = new Pen(handColor, hand.Grip ? 3.2f : 2.2f);
            using var handBrush = new SolidBrush(Color.FromArgb(100, handColor));
            float radius = (float)Math.Max(5, station.Snapshot.Profile.HandRadius * scale * .7);
            g.FillEllipse(handBrush, point.X - radius, point.Y - radius, radius * 2, radius * 2);
            g.DrawEllipse(handPen, point.X - radius, point.Y - radius, radius * 2, radius * 2);
            TextRenderer.DrawText(g, hand.Side == HandSide.Right ? "R" : "L", _smallFont,
                new Rectangle((int)point.X - 10, (int)point.Y - 8, 20, 16), handColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        foreach (ContactSnapshot contact in station.Snapshot.Contacts)
        {
            HandSnapshot? hand = station.Snapshot.Hands.FirstOrDefault(item => item.Side == contact.Hand);
            if (hand is null) continue;
            PointF from = Project(hand.Position, station.Task.SubjectOffset, centerX, centerY, scale);
            PointF to = Project(contact.Point, station.Task.SubjectOffset, centerX, centerY, scale);
            using var contactPen = new Pen(Color.FromArgb(255, 187, 87), 2) { DashStyle = DashStyle.Dash };
            g.DrawLine(contactPen, from, to);
            using var contactBrush = new SolidBrush(Color.FromArgb(255, 187, 87));
            g.FillEllipse(contactBrush, to.X - 3, to.Y - 3, 6, 6);
            TextRenderer.DrawText(g, $"{contact.Force:0.0}N", _smallFont,
                new Rectangle((int)to.X + 5, (int)to.Y - 9, 40, 16), Color.FromArgb(255, 214, 145),
                TextFormatFlags.NoPadding);
        }
    }

    private static PointF Project(Vec3 world, Vec3 offset, float centerX, float centerY, double scale)
    {
        Vec3 local = world - offset;
        double depth = Math.Clamp(1 - local.Z * .16, .82, 1.18);
        return new PointF(centerX + (float)(local.X * scale * depth), centerY - (float)(local.Y * scale * depth));
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        RenderSlot? slot = _slots.FirstOrDefault(item => item.Bounds.Contains(e.Location));
        if (slot is null || slot.Data.ExperimentId != _selected) return;
        _dragging = true;
        _dragExperiment = slot.Data.ExperimentId;
        Cursor = Cursors.SizeAll;
        RaiseDrag(slot, e.Location);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        RenderSlot? slot = _slots.FirstOrDefault(item => item.Data.ExperimentId == _dragExperiment);
        if (slot is not null) RaiseDrag(slot, e.Location);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = false;
        Cursor = Cursors.Default;
    }

    private void RaiseDrag(RenderSlot slot, Point location)
    {
        Rectangle scene = new(slot.Bounds.Left + 10, slot.Bounds.Top + 42, slot.Bounds.Width - 20, Math.Max(40, slot.Bounds.Height - 76));
        float centerX = scene.Left + scene.Width / 2f;
        float floorY = scene.Bottom - 5;
        double scale = Math.Min(scene.Width / 3.35, scene.Height / 2.58);
        float centerY = floorY - (float)(0.05 * scale);
        double x = (location.X - centerX) / scale;
        double y = (centerY - location.Y) / scale;
        double z = -.24;
        Vec3 target = slot.Data.Task.SubjectOffset + new Vec3(x, y, z);
        DragTargetRequested?.Invoke(this, new WorldDragEventArgs(slot.Data.ExperimentId, target, location));
    }

    private static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        int r = Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2);
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, r * 2, r * 2, 180, 90);
        path.AddArc(rectangle.Right - r * 2, rectangle.Top, r * 2, r * 2, 270, 90);
        path.AddArc(rectangle.Right - r * 2, rectangle.Bottom - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color SoftnessColor(double softness)
    {
        int green = (int)(145 + Math.Clamp(softness, 0, 1) * 80);
        return Color.FromArgb(112, 130, green, 215);
    }

    private static Color TaskColor(string taskId) => taskId.ToLowerInvariant() switch
    {
        "head_petting" => Color.FromArgb(106, 195, 255),
        "cheek_nuzzle" => Color.FromArgb(237, 122, 198),
        "limp_support" => Color.FromArgb(255, 181, 82),
        "hand_hold" => Color.FromArgb(125, 218, 169),
        _ => Color.FromArgb(147, 162, 255),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _taskFont.Dispose();
            _smallFont.Dispose();
            _scoreFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record RenderSlot(StationRenderData Data, Rectangle Bounds);
}
