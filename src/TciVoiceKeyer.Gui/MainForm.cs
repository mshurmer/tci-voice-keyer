using TciVoiceKeyer.Engine;

namespace TciVoiceKeyer.Gui;

public sealed class MainForm : Form
{
    private readonly VoiceKeyerEngine _engine = new();

    private readonly TextBox _radioHost = new() { Text = "127.0.0.1", Dock = DockStyle.Fill };
    private readonly NumericUpDown _tciPort = new() { Minimum = 1, Maximum = 65535, Value = 50001, Dock = DockStyle.Fill };
    private readonly TextBox _wavPath = new() { Dock = DockStyle.Fill };
    private readonly Button _browseButton = new() { Text = "Browse...", AutoSize = true };
    private readonly NumericUpDown _repeatCount = new() { Minimum = 1, Maximum = 100, Value = 3, Dock = DockStyle.Fill };
    private readonly NumericUpDown _listenSeconds = new() { Minimum = 0, Maximum = 600, DecimalPlaces = 1, Increment = 0.5M, Value = 5, Dock = DockStyle.Fill };
    private readonly TrackBar _volume = new() { Minimum = 0, Maximum = 200, Value = 100, TickFrequency = 25, SmallChange = 5, LargeChange = 10, Dock = DockStyle.Fill };
    private readonly Label _volumeValue = new() { Text = "100%", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };

    private readonly Button _startButton = new() { Text = "START CQ", AutoSize = true, MinimumSize = new Size(130, 44) };
    private readonly Button _stopButton = new() { Text = "STOP", AutoSize = true, MinimumSize = new Size(130, 44), Enabled = false };

    private readonly Label _stateValue = new() { Text = "Idle", AutoSize = true };
    private readonly Label _repeatValue = new() { Text = "-", AutoSize = true };
    private readonly Label _countdownValue = new() { Text = "-", AutoSize = true };
    private readonly Label _detailValue = new() { Text = "Select a WAV file and press START CQ.", AutoSize = true, MaximumSize = new Size(620, 0) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 150 };

    private Task? _runTask;
    private bool _allowClose;

    public MainForm()
    {
        Text = "TCI Voice Keyer";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 610);
        Size = new Size(820, 680);
        AutoScaleMode = AutoScaleMode.Dpi;

        _engine.StatusChanged += EngineOnStatusChanged;
        _browseButton.Click += BrowseButtonOnClick;
        _startButton.Click += StartButtonOnClick;
        _stopButton.Click += StopButtonOnClick;
        _volume.Scroll += (_, _) => _volumeValue.Text = $"{_volume.Value}%";

        Controls.Add(BuildLayout());
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = false
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "TCI Voice Keyer",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };
        root.Controls.Add(title, 0, 0);

        var settings = new GroupBox { Text = "Keyer settings", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 7 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow(grid, 0, "Radio IP / host", _radioHost, new Label());
        AddRow(grid, 1, "TCI port", _tciPort, new Label());
        AddRow(grid, 2, "WAV file", _wavPath, _browseButton);
        AddRow(grid, 3, "Repeat count", _repeatCount, new Label { Text = "calls", AutoSize = true });
        AddRow(grid, 4, "Listen time", _listenSeconds, new Label { Text = "seconds", AutoSize = true });

        var volumePanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
        volumePanel.Controls.Add(_volume, 0, 0);
        volumePanel.Controls.Add(_volumeValue, 1, 0);
        AddRow(grid, 5, "WAV volume", volumePanel, new Label());

        AddRow(grid, 6, "TX cleanup", new Label { Text = "500 ms automatic trailing silence", AutoSize = true }, new Label());
        settings.Controls.Add(grid);
        root.Controls.Add(settings, 0, 1);

        var actionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 12, 0, 12)
        };
        actionPanel.Controls.Add(_startButton);
        actionPanel.Controls.Add(_stopButton);
        root.Controls.Add(actionPanel, 0, 2);

        var status = new GroupBox { Text = "Status", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var statusGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddStatusRow(statusGrid, 0, "State", _stateValue);
        AddStatusRow(statusGrid, 1, "Repeat", _repeatValue);
        AddStatusRow(statusGrid, 2, "Countdown", _countdownValue);
        AddStatusRow(statusGrid, 3, "Detail", _detailValue);
        statusGrid.Controls.Add(new Label { Text = "Event log", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 4);
        statusGrid.Controls.Add(_log, 1, 4);
        status.Controls.Add(statusGrid);
        root.Controls.Add(status, 0, 3);

        return root;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control value, Control suffix)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 8) }, 0, row);
        value.Margin = new Padding(3, 5, 3, 5);
        value.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(value, 1, row);
        suffix.Margin = new Padding(6, 8, 3, 8);
        suffix.Anchor = AnchorStyles.Left;
        grid.Controls.Add(suffix, 2, row);
    }

    private static void AddStatusRow(TableLayoutPanel grid, int row, string label, Control value)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4) }, 0, row);
        value.Margin = new Padding(3, 4, 3, 4);
        value.Anchor = AnchorStyles.Left;
        grid.Controls.Add(value, 1, row);
    }

    private void BrowseButtonOnClick(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select CQ WAV file",
            Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (File.Exists(_wavPath.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(_wavPath.Text);

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _wavPath.Text = dialog.FileName;
    }

    private async void StartButtonOnClick(object? sender, EventArgs e)
    {
        if (_engine.IsRunning)
            return;

        if (!TryBuildOptions(out var options, out var error))
        {
            MessageBox.Show(this, error, "Cannot start keyer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetRunningUi(true);
        _log.Clear();
        AppendLog($"Starting: {Path.GetFileName(options!.WavFile)}, repeat {options.RepeatCount}, listen {options.ListenDelay.TotalSeconds:F1}s, volume {options.VolumePercent:F0}%.");

        try
        {
            _runTask = _engine.StartAsync(options);
            await _runTask;
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Keyer error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _runTask = null;
            if (!IsDisposed)
                SetRunningUi(false);
        }
    }

    private async void StopButtonOnClick(object? sender, EventArgs e)
    {
        _stopButton.Enabled = false;
        AppendLog("STOP requested - forcing RX.");
        await _engine.StopAsync();
    }

    private bool TryBuildOptions(out KeyerOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        var host = _radioHost.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Enter the Thetis radio IP address or host name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_wavPath.Text) || !File.Exists(_wavPath.Text))
        {
            error = "Select an existing WAV file.";
            return false;
        }

        if (!Uri.TryCreate($"ws://{host}:{(int)_tciPort.Value}/", UriKind.Absolute, out var serverUri))
        {
            error = "The radio IP/host and TCI port do not form a valid WebSocket address.";
            return false;
        }

        options = new KeyerOptions(
            serverUri,
            Path.GetFullPath(_wavPath.Text),
            (int)_repeatCount.Value,
            TimeSpan.FromSeconds((double)_listenSeconds.Value),
            KeyerOptions.DefaultTailSilence,
            _volume.Value);

        try
        {
            options.Validate();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            options = null;
            return false;
        }
    }

    private void EngineOnStatusChanged(KeyerStatus status)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ApplyStatus(status)));
            return;
        }

        ApplyStatus(status);
    }

    private void ApplyStatus(KeyerStatus status)
    {
        _stateValue.Text = status.State.ToString();
        _repeatValue.Text = status.TotalRepeats > 0 && status.CurrentRepeat > 0
            ? $"{status.CurrentRepeat} of {status.TotalRepeats}"
            : status.TotalRepeats > 0 ? $"0 of {status.TotalRepeats}" : "-";
        _countdownValue.Text = status.Remaining.HasValue
            ? $"{Math.Max(0, status.Remaining.Value.TotalSeconds):F1} s"
            : "-";
        _detailValue.Text = status.Message;
        AppendLog($"{status.State}: {status.Message}" + (status.Remaining.HasValue ? $" ({Math.Max(0, status.Remaining.Value.TotalSeconds):F1}s)" : string.Empty));
    }

    private void AppendLog(string message)
    {
        if (IsDisposed)
            return;
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }

    private void SetRunningUi(bool running)
    {
        _startButton.Enabled = !running;
        _stopButton.Enabled = running;
        _radioHost.Enabled = !running;
        _tciPort.Enabled = !running;
        _wavPath.Enabled = !running;
        _browseButton.Enabled = !running;
        _repeatCount.Enabled = !running;
        _listenSeconds.Enabled = !running;
        _volume.Enabled = !running;
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && _engine.IsRunning)
        {
            e.Cancel = true;
            SetRunningUi(true);
            _stopButton.Enabled = false;
            AppendLog("Window close requested - forcing RX before exit.");

            await _engine.StopAsync();
            if (_runTask is not null)
            {
                try { await _runTask; }
                catch { }
            }

            _allowClose = true;
            Close();
            return;
        }

        await _engine.DisposeAsync();
        base.OnFormClosing(e);
    }
}
