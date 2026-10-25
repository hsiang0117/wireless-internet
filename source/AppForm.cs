using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WirelessInternet
{
    public sealed class AppForm : Form
    {
        private static readonly Color BackgroundColor = Color.FromArgb(12, 20, 31);
        private static readonly Color PanelColor = Color.FromArgb(20, 31, 45);
        private static readonly Color BorderColor = Color.FromArgb(40, 57, 75);
        private static readonly Color TextColor = Color.FromArgb(235, 243, 250);
        private static readonly Color MutedColor = Color.FromArgb(146, 166, 186);
        private static readonly Color AccentColor = Color.FromArgb(48, 216, 192);
        private static readonly Color UploadColor = Color.FromArgb(113, 161, 255);
        private readonly string _root;
        private readonly bool _preview;
        private readonly Backend _backend;
        private readonly UiOperationCoordinator _operations;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _logTimer;
        private readonly Button _startButton;
        private readonly Button _stopButton;
        private readonly Button _settingsButton;
        private readonly Label _status;
        private readonly Label _detail;
        private readonly Label _phone;
        private readonly Label _endpoint;
        private readonly Label _protection;
        private readonly StatusLight _light;
        private readonly MetricCard _totalCard;
        private readonly MetricCard _downloadCard;
        private readonly MetricCard _uploadCard;
        private readonly MetricCard _durationCard;
        private readonly SpeedChart _chart;
        private readonly Label _trafficNote;
        private readonly Button _logsTab;
        private readonly Button _connectionsTab;
        private readonly CheckBox _autoScroll;
        private readonly RichTextBox _console;
        private readonly DataGridView _connections;
        private readonly Panel _viewport;
        private readonly TableLayoutPanel _layout;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly ToolStripMenuItem _trayStart;
        private readonly ToolStripMenuItem _trayStop;
        private readonly ToolStripMenuItem _traySettings;
        private readonly ToolStripMenuItem _trayExit;
        private readonly HashSet<string> _seenLogs = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _seenLogOrder = new Queue<string>();
        private const int MaximumConsoleLines = 1500;
        private const int MaximumConsoleCharacters = 200000;
        private const int MaximumConsoleEntryCharacters = 4096;
        private const int MaximumPendingLogEntries = 256;
        private const int MaximumPendingLogCharacters = 65536;
        private const int MaximumFlushEntries = 64;
        private const int MaximumFlushCharacters = 12288;
        private readonly object _pendingLogGate = new object();
        private readonly Queue<PendingLog> _pendingLogs = new Queue<PendingLog>();
        private int _pendingLogCharacters;
        private int _droppedPendingLogCount;
        private int _pendingDroppedToReport;
        private readonly Queue<int> _consoleLineLengths = new Queue<int>();
        private int _consoleLineCount;
        private int _consoleCharacterCount;
        private Task _pollTask { get { return _operations.PendingPoll; } }
        private Snapshot _snapshot;
        private bool _polling { get { return _operations.IsPolling; } }
        private bool _actionBusy { get { return _operations.IsActionBusy; } }
        private bool _closing { get { return _operations.IsClosing; } }
        private bool _exitRequested { get { return _operations.IsExitRequested; } }
        private bool _allowClose { get { return _operations.AllowClose; } }
        private bool _trayHintShown;
        private int _disposedBackend;
        private int _disposedUiResources;
        private string _lastSessionId;
        private TimeSpan _lastDuration = TimeSpan.Zero;
        private string _lastPollError;
        private bool _logsVisible = true;
        private bool _setupOpen { get { return _operations.IsConfiguring; } }
        private bool _configurationMissing;
        private string _phoneBindingNotice = String.Empty;
        private DateTime _phoneBindingNoticeUntil;
        internal bool AutomaticSetupEnabled { get; set; }
        private bool _flushingLogs;
        private string _connectionsSignature = String.Empty;

        private sealed class PendingLog
        {
            public string TimePrefix, SourcePrefix, Body;
            public int ColorIndex, Characters;
            public List<int> LineLengths;
        }

        internal int PendingLogCount { get { lock (_pendingLogGate) return _pendingLogs.Count; } }
        internal int PendingLogCharacters { get { lock (_pendingLogGate) return _pendingLogCharacters; } }
        internal int DroppedPendingLogCount { get { lock (_pendingLogGate) return _droppedPendingLogCount; } }

        public AppForm(string root, bool preview) : this(root, preview, new UiOperationCoordinator()) { }

        internal AppForm(string root, bool preview, UiOperationCoordinator operations)
        {
            if (operations == null) throw new ArgumentNullException("operations");
            _operations = operations;
            SuspendLayout();
            _root = root;
            _preview = preview;
            _backend = new Backend(root, preview);
            _configurationMissing = !preview && !SetupBackend.IsConfigurationComplete(root);
            AutomaticSetupEnabled = !preview;
            Text = "无线网络共享" + (preview ? " · 界面预览" : String.Empty);
            BackColor = BackgroundColor;
            ForeColor = TextColor;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1220, 860);
            Icon = SystemIcons.Application;

            Panel viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            _viewport = viewport;
            Controls.Add(viewport);
            TableLayoutPanel layout = new TableLayoutPanel();
            _layout = layout;
            layout.Dock = DockStyle.Top;
            layout.MinimumSize = new Size(1060, 700);
            layout.Height = 700;
            layout.Padding = new Padding(24, 15, 24, 10);
            layout.ColumnCount = 1;
            layout.RowCount = 6;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 118F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 161F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39F));
            viewport.Controls.Add(layout);
            viewport.Resize += delegate { layout.Height = Math.Max(viewport.ClientSize.Height, UiDpi.Scale(this, 700)); };

            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0, 0, 0, 11);
            LogoMark logo = new LogoMark();
            logo.Bounds = new Rectangle(0, 7, 43, 43);
            header.Controls.Add(logo);
            Label title = MakeLabel("无线网络共享", 20F, TextColor, FontStyle.Bold);
            title.SetBounds(56, 0, 320, 36);
            header.Controls.Add(title);
            Label subtitle = MakeLabel("手机无线连接 · 实时流量与连接监控", 9F, MutedColor, FontStyle.Regular);
            subtitle.SetBounds(58, 39, 520, 22);
            header.Controls.Add(subtitle);
            FlowLayoutPanel controls = new FlowLayoutPanel();
            controls.Dock = DockStyle.Right;
            controls.Width = 432;
            controls.FlowDirection = FlowDirection.RightToLeft;
            controls.Padding = new Padding(0, 10, 0, 0);
            controls.WrapContents = false;
            _stopButton = MakeButton("断开连接", false);
            _stopButton.Size = new Size(118, 42);
            _stopButton.Margin = new Padding(12, 0, 0, 0);
            _stopButton.Click += delegate { RunAction("Stop"); };
            _startButton = MakeButton("开始连接", true);
            _startButton.Size = new Size(118, 42);
            _startButton.Margin = new Padding(0);
            _startButton.Click += delegate { RunAction("Start"); };
            _settingsButton = MakeButton("更换/绑定手机", false);
            _settingsButton.Size = new Size(136, 42);
            _settingsButton.Margin = new Padding(0, 0, 12, 0);
            _settingsButton.Click += async delegate { await ConfigurePhoneAsync(); };
            controls.Controls.Add(_stopButton);
            controls.Controls.Add(_startButton);
            controls.Controls.Add(_settingsButton);
            header.Controls.Add(controls);
            header.Resize += delegate {
                title.Width = Math.Max(1, controls.Left - title.Left - UiDpi.Scale(this, 16));
                subtitle.Width = Math.Max(1, controls.Left - subtitle.Left - UiDpi.Scale(this, 16));
            };
            layout.Controls.Add(header, 0, 0);

            RoundedPanel statusPanel = new RoundedPanel();
            statusPanel.Dock = DockStyle.Fill;
            statusPanel.Margin = new Padding(0, 0, 0, 10);
            statusPanel.Padding = new Padding(17, 11, 17, 10);
            TableLayoutPanel statusLayout = new TableLayoutPanel();
            statusLayout.Dock = DockStyle.Fill;
            statusLayout.ColumnCount = 3;
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49F));
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26F));
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            statusLayout.RowCount = 1;
            statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            statusPanel.Controls.Add(statusLayout);
            Panel connectionStatus = new Panel();
            connectionStatus.Dock = DockStyle.Fill;
            connectionStatus.Margin = new Padding(0);
            _light = new StatusLight();
            _light.SetBounds(0, 7, 12, 12);
            _status = MakeLabel("正在读取连接状态…", 12F, TextColor, FontStyle.Bold);
            _status.SetBounds(22, 0, 460, 27);
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _detail = MakeLabel("等待首次采样", 8.5F, MutedColor, FontStyle.Regular);
            _detail.SetBounds(22, 29, 460, 21);
            _detail.AutoEllipsis = true;
            _detail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            connectionStatus.Controls.Add(_light);
            connectionStatus.Controls.Add(_status);
            connectionStatus.Controls.Add(_detail);
            connectionStatus.Resize += delegate
            {
                _status.Width = Math.Max(1, connectionStatus.ClientSize.Width - _status.Left);
                _detail.Width = Math.Max(1, connectionStatus.ClientSize.Width - _detail.Left);
            };
            statusLayout.Controls.Add(connectionStatus, 0, 0);
            Panel phoneInfo = new Panel();
            phoneInfo.Dock = DockStyle.Fill;
            phoneInfo.Margin = new Padding(0);
            _phone = MakeLabel("手机 · 等待连接", 9F, TextColor, FontStyle.Regular);
            _phone.Dock = DockStyle.Top;
            _phone.Height = 27;
            _phone.AutoEllipsis = true;
            _endpoint = MakeLabel("无线端点  —", 8.5F, MutedColor, FontStyle.Regular);
            _endpoint.Dock = DockStyle.Bottom;
            _endpoint.Height = 24;
            phoneInfo.Controls.Add(_phone);
            phoneInfo.Controls.Add(_endpoint);
            statusLayout.Controls.Add(phoneInfo, 1, 0);
            _protection = MakeLabel("失联保护\n等待会话", 9F, MutedColor, FontStyle.Regular);
            _protection.Dock = DockStyle.Fill;
            _protection.TextAlign = ContentAlignment.MiddleRight;
            statusLayout.Controls.Add(_protection, 2, 0);
            layout.Controls.Add(statusPanel, 0, 1);

            TableLayoutPanel cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Fill;
            cards.Margin = new Padding(0, 0, 0, 10);
            cards.ColumnCount = 4;
            cards.RowCount = 1;
            for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            _totalCard = new MetricCard("本次 VPN 流量", "—", "下载 —   /   上传 —", TextColor);
            _downloadCard = new MetricCard("当前下载速度", "—", "手机接收", AccentColor);
            _uploadCard = new MetricCard("当前上传速度", "—", "手机发送", UploadColor);
            _durationCard = new MetricCard("本次连接时长", "00:00:00", "等待开始连接", TextColor);
            MetricCard[] metricCards = { _totalCard, _downloadCard, _uploadCard, _durationCard };
            for (int i = 0; i < metricCards.Length; i++)
            {
                metricCards[i].Dock = DockStyle.Fill;
                metricCards[i].Margin = new Padding(i == 0 ? 0 : 6, 0, i == 3 ? 0 : 6, 0);
                cards.Controls.Add(metricCards[i], i, 0);
            }
            layout.Controls.Add(cards, 0, 2);

            RoundedPanel chartPanel = new RoundedPanel();
            chartPanel.Dock = DockStyle.Fill;
            chartPanel.Margin = new Padding(0, 0, 0, 10);
            chartPanel.Padding = new Padding(17, 11, 17, 8);
            _chart = new SpeedChart();
            _chart.Dock = DockStyle.Fill;
            chartPanel.Controls.Add(_chart);
            layout.Controls.Add(chartPanel, 0, 3);

            RoundedPanel monitorPanel = new RoundedPanel();
            monitorPanel.Dock = DockStyle.Fill;
            monitorPanel.Margin = new Padding(0);
            monitorPanel.Padding = new Padding(13, 6, 13, 9);
            Panel monitorToolbar = new Panel();
            monitorToolbar.Dock = DockStyle.Top;
            monitorToolbar.Height = 42;
            FlowLayoutPanel tabs = new FlowLayoutPanel();
            tabs.Dock = DockStyle.Left;
            tabs.Width = 290;
            tabs.Padding = new Padding(0, 3, 0, 0);
            tabs.WrapContents = false;
            _logsTab = MakeTabButton("实时日志", true);
            _connectionsTab = MakeTabButton("TCP 连接  0", false);
            _logsTab.Click += delegate { SetLogView(true); };
            _connectionsTab.Click += delegate { SetLogView(false); };
            tabs.Controls.Add(_logsTab);
            tabs.Controls.Add(_connectionsTab);
            monitorToolbar.Controls.Add(tabs);
            FlowLayoutPanel logTools = new FlowLayoutPanel();
            logTools.Dock = DockStyle.Right;
            logTools.Width = 375;
            logTools.FlowDirection = FlowDirection.RightToLeft;
            logTools.Padding = new Padding(0, 3, 0, 0);
            logTools.WrapContents = false;
            Button clear = MakeSmallButton("清空显示");
            clear.Width = 86;
            clear.Click += delegate { ClearConsoleDisplay(); };
            _autoScroll = new CheckBox();
            _autoScroll.Text = "自动滚动";
            _autoScroll.Checked = true;
            _autoScroll.AutoSize = false;
            _autoScroll.Size = new Size(91, 31);
            _autoScroll.ForeColor = MutedColor;
            _autoScroll.Margin = new Padding(3, 0, 8, 0);
            _autoScroll.FlatStyle = FlatStyle.Flat;
            logTools.Controls.Add(clear);
            logTools.Controls.Add(_autoScroll);
            Label liveOnly = MakeLabel("仅实时显示，不保存", 8F, MutedColor, FontStyle.Regular);
            liveOnly.Size = new Size(151, 31);
            liveOnly.TextAlign = ContentAlignment.MiddleLeft;
            liveOnly.Margin = new Padding(0, 0, 8, 0);
            logTools.Controls.Add(liveOnly);
            monitorToolbar.Controls.Add(logTools);
            Panel monitorContent = new Panel();
            monitorContent.Dock = DockStyle.Fill;
            monitorContent.Padding = new Padding(1, 3, 1, 1);
            _console = new RichTextBox();
            _console.Dock = DockStyle.Fill;
            _console.ReadOnly = true;
            _console.BorderStyle = BorderStyle.None;
            _console.BackColor = PanelColor;
            _console.ForeColor = Color.FromArgb(184, 204, 221);
            _console.Font = new Font("Consolas", 9F);
            _console.WordWrap = false;
            _console.DetectUrls = false;
            _console.HideSelection = false;
            _connections = MakeConnectionGrid();
            _connections.Dock = DockStyle.Fill;
            _connections.Visible = false;
            monitorContent.Controls.Add(_connections);
            monitorContent.Controls.Add(_console);
            monitorPanel.Controls.Add(monitorContent);
            monitorPanel.Controls.Add(monitorToolbar);
            layout.Controls.Add(monitorPanel, 0, 4);

            TableLayoutPanel footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.Margin = new Padding(0, 8, 0, 0);
            footer.ColumnCount = 2;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, preview ? 270F : 310F));
            _trafficNote = MakeLabel("流量按手机 VPN 接口统计，不等同于运营商计费。", 8F, MutedColor, FontStyle.Regular);
            _trafficNote.Dock = DockStyle.Fill;
            _trafficNote.AutoEllipsis = true;
            _trafficNote.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(_trafficNote, 0, 0);
            Label closeNote = MakeLabel(preview ? "● 界面预览 · 不执行真实连接操作" : "关闭窗口收起到托盘 · 托盘退出会自动断开共享", 8F, preview ? Color.FromArgb(255, 200, 103) : MutedColor, FontStyle.Regular);
            closeNote.Dock = DockStyle.Fill;
            closeNote.TextAlign = ContentAlignment.MiddleRight;
            footer.Controls.Add(closeNote, 1, 0);
            layout.Controls.Add(footer, 0, 5);

            _trayMenu = new ContextMenuStrip();
            _trayMenu.ShowImageMargin = false;
            ToolStripMenuItem showWindow = new ToolStripMenuItem("显示主窗口");
            showWindow.Click += delegate { RestoreFromTray(); };
            _trayStart = new ToolStripMenuItem("开始连接");
            _trayStop = new ToolStripMenuItem("断开连接");
            _traySettings = new ToolStripMenuItem("更换/绑定手机");
            _trayExit = new ToolStripMenuItem("退出");
            _trayStart.Click += delegate { RunAction("Start"); };
            _trayStop.Click += delegate { RunAction("Stop"); };
            _traySettings.Click += async delegate { await ConfigurePhoneAsync(); };
            _trayExit.Click += delegate { ExitApplication(); };
            _trayMenu.Items.Add(showWindow);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_trayStart);
            _trayMenu.Items.Add(_trayStop);
            _trayMenu.Items.Add(_traySettings);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_trayExit);
            _trayMenu.Opening += delegate { UpdateButtons(); };
            _trayIcon = new NotifyIcon { Text = "无线网络共享 · 正在读取状态", Icon = Icon, ContextMenuStrip = _trayMenu, Visible = false };
            _trayIcon.DoubleClick += delegate { RestoreFromTray(); };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { PollOnce(); };
            _logTimer = new System.Windows.Forms.Timer();
            _logTimer.Interval = 120;
            _logTimer.Tick += delegate { FlushPendingLogs(); };
            Shown += delegate
            {
                RefreshDpiLayout();
                _trayIcon.Icon = Icon;
                _trayIcon.Visible = true;
                AppendLog(DateTime.Now, "界面", preview ? "预览模式：显示模拟数据，不连接手机、不更改当前共享。" : "控制台已就绪。连接、断开与失联保护沿用当前工具配置。", preview ? "Warning" : "Info");
                PollOnce();
                _timer.Start();
                _logTimer.Start();
                if (AutomaticSetupEnabled && _configurationMissing)
                    BeginInvoke((Action)(async delegate { await ConfigurePhoneAsync(); }));
            };
            FormClosing += OnFormClosing;
            FormClosed += delegate { ReleaseUiResources(); };
            UpdateButtons();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ResumeLayout(true);
            UiDpi.FitToWorkingArea(this, new Size(1220, 860), new Size(1100, 760));
            RefreshDpiLayout();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // WinForms applies its single scaling pass after raising this event.
            if (IsHandleCreated) BeginInvoke((Action)delegate { if (!IsDisposed) RefreshDpiLayout(); });
        }

        private void RefreshDpiLayout()
        {
            if (_connections == null) return;
            UiDpi.ClampToWorkingArea(this, new Size(1100, 760));
            Size contentMinimum = new Size(UiDpi.Scale(this, 1060), UiDpi.Scale(this, 700));
            _layout.MinimumSize = contentMinimum;
            _viewport.AutoScrollMinSize = contentMinimum;
            _layout.Height = Math.Max(_viewport.ClientSize.Height, contentMinimum.Height);
            int rowHeight = UiDpi.Scale(this, 29);
            _connections.ColumnHeadersHeight = UiDpi.Scale(this, 31);
            _connections.RowTemplate.Height = rowHeight;
            foreach (DataGridViewRow row in _connections.Rows) row.Height = rowHeight;
            _connections.ColumnHeadersDefaultCellStyle.Padding = new Padding(UiDpi.Scale(this, 8), 0, 0, 0);
            _connections.DefaultCellStyle.Padding = new Padding(UiDpi.Scale(this, 8), 0, 0, 0);
            _connections.Columns["Protocol"].Width = UiDpi.Scale(this, 95);
            _connections.Columns["RemotePort"].Width = UiDpi.Scale(this, 110);
            _connections.Columns["State"].Width = UiDpi.Scale(this, 160);
            Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int enabled = 1; DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendPointMessage(IntPtr window, int message, IntPtr wParam, ref NativePoint point);

        private async void PollOnce()
        {
            if (!_operations.TryBeginPoll()) return;
            try
            {
                Task<Snapshot> task = Task.Run(delegate { return _backend.Poll(); });
                _operations.TrackPoll(task);
                Snapshot next = await task;
                if (!_closing && !_exitRequested && !IsDisposed)
                {
                    _lastPollError = null;
                    ApplySnapshot(next);
                }
            }
            catch (Exception ex)
            {
                if (!_closing && !_exitRequested && !IsDisposed)
                {
                    if (!_actionBusy)
                    {
                        _status.Text = "状态暂不可用";
                        _detail.Text = ex.Message;
                        _light.LightColor = Color.FromArgb(255, 193, 99);
                    }
                    if (!String.Equals(_lastPollError, ex.Message, StringComparison.Ordinal))
                    {
                        AppendLog(DateTime.Now, "采样", ex.Message, "Warning");
                        _lastPollError = ex.Message;
                    }
                }
            }
            finally { _operations.CompletePoll(); }
        }

        private async void RunAction(string action)
        {
            await RunActionAsync(action, false);
        }

        private async Task<int> RunActionAsync(string action, bool exiting)
        {
            if (action != "Start" && action != "Stop") throw new ArgumentException("Unknown action.", "action");
            UiOperationKind kind = action == "Start" ? UiOperationKind.Start : UiOperationKind.Stop;
            if (!_operations.CanBeginAction(kind, exiting)) return -1;
            if (action == "Start" && !_preview && !SetupBackend.IsConfigurationComplete(_root))
            {
                await ConfigurePhoneAsync();
                return 0; // Configuration alone never starts sharing.
            }
            if (_preview)
            {
                AppendLog(DateTime.Now, "预览", "当前为界面预览，不执行" + (action == "Start" ? "连接" : "断开") + "操作。", "Warning");
                return 0;
            }
            int exitCode = -1;
            if (!_operations.TryBeginAction(kind, exiting)) return -1;
            UpdateButtons();
            _status.Text = action == "Start" ? "正在建立连接…" : "正在断开连接…";
            _detail.Text = "请稍候，正在执行连接控制流程";
            _light.LightColor = Color.FromArgb(255, 193, 99);
            AppendLog(DateTime.Now, "控制", action == "Start" ? "开始连接手机。" : "正在停止本次共享。", "Info");
            try
            {
                await _operations.WaitForPollAsync();
                exitCode = await Task.Run(delegate
                {
                    return _backend.RunAction(action, delegate(string output)
                    {
                        // The worker queues bounded text directly; it never floods BeginInvoke.
                        AppendLog(DateTime.Now, "控制", output, "Info");
                    });
                });
                AppendLog(DateTime.Now, "控制", exitCode == 0 ? (action == "Start" ? "连接流程已完成。" : "断开流程已完成。") : "操作未完成（退出码 " + exitCode.ToString(CultureInfo.InvariantCulture) + "），请查看上方输出。", exitCode == 0 ? "Success" : "Error");
            }
            catch (Exception ex) { AppendLog(DateTime.Now, "控制", ex.Message, "Error"); }
            finally
            {
                _operations.CompleteAction(exitCode);
                UpdateButtons();
                if (!_exitRequested) PollOnce();
            }
            return exitCode;
        }

        private void ApplySnapshot(Snapshot value)
        {
            if (value == null) return;
            _snapshot = value;
            if (!String.IsNullOrEmpty(value.SessionId) && !String.Equals(_lastSessionId, value.SessionId, StringComparison.Ordinal))
            {
                _chart.ClearSamples();
                _lastSessionId = value.SessionId;
            }
            if (!_actionBusy && !_exitRequested)
            {
                _status.Text = String.IsNullOrWhiteSpace(value.StatusText) ? (value.HasSession ? "连接状态待确认" : "尚未连接") : value.StatusText;
                _detail.Text = String.IsNullOrWhiteSpace(value.Detail) ? (value.HasSession ? "正在监控当前会话" : "手机保持 Wi-Fi、热点与无线调试开启后，点击开始连接。") : value.Detail;
                if (DateTime.UtcNow < _phoneBindingNoticeUntil) _detail.Text = _phoneBindingNotice;
                _detail.ForeColor = DateTime.UtcNow < _phoneBindingNoticeUntil ? Color.FromArgb(255, 193, 99) : MutedColor;
                _light.LightColor = value.Healthy ? AccentColor : value.HasSession ? Color.FromArgb(255, 193, 99) : MutedColor;
            }
            _phone.Text = "手机 · " + (String.IsNullOrWhiteSpace(value.Phone) ? "等待连接" : value.Phone);
            _endpoint.Text = "无线端点  " + (String.IsNullOrWhiteSpace(value.Endpoint) ? "—" : value.Endpoint);
            string protectionTimeout = (value.ProtectionTimeoutSeconds > 0 ? value.ProtectionTimeoutSeconds : 30).ToString(CultureInfo.InvariantCulture);
            if (value.HasSession && value.ProtectionState == ProtectionState.Healthy)
            {
                _protection.Text = "● " + protectionTimeout + " 秒失联保护已启用\n心跳 " + FormatAge(value.HeartbeatAgeSeconds) + " 前";
                _protection.ForeColor = AccentColor;
            }
            else if (value.HasSession)
            {
                _protection.Text = (value.ProtectionState == ProtectionState.Degraded ? "● 失联保护异常" :
                    value.ProtectionState == ProtectionState.Inactive ? "● 失联保护未启用" : "● 失联保护待确认") +
                    "\n心跳 " + FormatAge(value.HeartbeatAgeSeconds) + " 前";
                _protection.ForeColor = Color.FromArgb(255, 193, 99);
            }
            else
            {
                _protection.Text = protectionTimeout + " 秒失联保护\n随共享会话自动启用";
                _protection.ForeColor = MutedColor;
            }
            ulong total = UInt64.MaxValue - value.DownloadBytes < value.UploadBytes ? UInt64.MaxValue : value.DownloadBytes + value.UploadBytes;
            _totalCard.SetValue(value.TrafficAvailable ? FormatBytes(total) : "—", value.TrafficAvailable ? "下载 " + FormatBytes(value.DownloadBytes) + "   /   上传 " + FormatBytes(value.UploadBytes) : "等待有效流量采样");
            bool telemetryCurrent = value.TelemetryState == TelemetryState.Current && value.SamplingOk;
            _downloadCard.SetValue(telemetryCurrent ? FormatRate(value.DownloadBytesPerSecond) : "—", telemetryCurrent ? "手机接收" : "等待有效采样");
            _uploadCard.SetValue(telemetryCurrent ? FormatRate(value.UploadBytesPerSecond) : "—", telemetryCurrent ? "手机发送" : "等待有效采样");
            TimeSpan duration = value.HasSession && value.StartedAt.HasValue ? DateTime.Now - value.StartedAt.Value.ToLocalTime() : _lastDuration;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            _lastDuration = duration;
            _durationCard.SetValue(((int)duration.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + duration.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + duration.Seconds.ToString("00", CultureInfo.InvariantCulture), value.HasSession ? "当前共享会话" : value.StartedAt.HasValue ? "上次共享已结束" : "等待开始连接");
            if (value.HasSession) _chart.AddSample(telemetryCurrent ? value.DownloadBytesPerSecond : Double.NaN, telemetryCurrent ? value.UploadBytesPerSecond : Double.NaN);
            if (!String.IsNullOrWhiteSpace(value.TrafficNote)) _trafficNote.Text = value.TrafficNote;
            UpdateConnections(value.Connections);
            if (value.Logs != null)
            {
                foreach (LogEntry item in value.Logs)
                {
                    string sessionKey = (value.SessionId ?? String.Empty).GetHashCode().ToString("x8", CultureInfo.InvariantCulture);
                    string sourceKey = (item.Source ?? String.Empty).GetHashCode().ToString("x8", CultureInfo.InvariantCulture);
                    string key = item.Sequence > 0 ? sessionKey + "|" + sourceKey + "|" + item.Sequence.ToString(CultureInfo.InvariantCulture) :
                        sessionKey + "|" + sourceKey + "|" + item.Time.Ticks.ToString(CultureInfo.InvariantCulture) + "|" +
                        (item.Message ?? String.Empty).GetHashCode().ToString("x8", CultureInfo.InvariantCulture) + "|" +
                        (item.Level ?? String.Empty).GetHashCode().ToString("x8", CultureInfo.InvariantCulture);
                    if (_seenLogs.Add(key))
                    {
                        _seenLogOrder.Enqueue(key);
                        while (_seenLogOrder.Count > MaximumConsoleLines) _seenLogs.Remove(_seenLogOrder.Dequeue());
                        AppendLog(item.Time, item.Source, item.Message, item.Level);
                    }
                }
            }
            UpdateButtons();
        }

        private static string FormatAge(double seconds)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0) return "—";
            return seconds < 10 ? seconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒" : seconds.ToString("0", CultureInfo.InvariantCulture) + " 秒";
        }

        private static string FormatBytes(ulong bytes)
        {
            return FormatSize((double)bytes, false);
        }

        private static string FormatRate(double bytes)
        {
            if (Double.IsNaN(bytes) || Double.IsInfinity(bytes) || bytes < 0) return "—";
            return FormatSize(bytes, true);
        }

        private static string FormatSize(double bytes, bool rate)
        {
            if (Double.IsNaN(bytes) || Double.IsInfinity(bytes) || bytes < 0) bytes = 0;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int index = 0;
            while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
            return bytes.ToString(index == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[index] + (rate ? "/s" : String.Empty);
        }

        private void UpdateButtons()
        {
            if (_closing || IsDisposed) return;
            bool hasSession = _snapshot != null && _snapshot.HasSession;
            UiOperationPresentation view = _operations.GetPresentation(_preview, hasSession,
                _snapshot != null && _snapshot.Healthy, _configurationMissing, _snapshot == null ? null : _snapshot.StatusText);
            _startButton.Enabled = view.StartEnabled;
            _stopButton.Enabled = view.StopEnabled;
            _startButton.Text = view.StartText;
            _stopButton.Text = view.StopText;
            _settingsButton.Enabled = view.SettingsEnabled;
            _traySettings.Enabled = _settingsButton.Enabled;
            _traySettings.ToolTipText = hasSession ? "请先断开当前共享，再更换或绑定手机。" : "重新打开手机配置向导；仅成功保存才替换原绑定。";
            _trayStart.Enabled = _startButton.Enabled;
            _trayStop.Enabled = _stopButton.Enabled;
            _trayExit.Enabled = view.ExitEnabled;
            _trayExit.Text = view.ExitText;
            string tooltip = "无线网络共享 · " + view.TrayState;
            _trayIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        private async Task<bool> ConfigurePhoneAsync()
        {
            if (!_operations.CanBeginConfiguration(_preview)) return false;
            try
            {
                if (PhoneBindingHasSession())
                {
                    ShowPhoneBindingNotice("请先断开当前共享，手机配置不会自动修改。");
                    return false;
                }
            }
            catch (Exception exception)
            {
                ShowPhoneBindingNotice("暂不能更换手机：" + exception.Message);
                return false;
            }
            if (!_operations.TryBeginConfiguration(_preview)) return false;
            UpdateButtons();
            try
            {
                await _operations.WaitForPollAsync();
                if (_closing || IsDisposed) return false;
                if (PhoneBindingHasSession())
                {
                    ShowPhoneBindingNotice("请先断开当前共享，手机配置不会自动修改。");
                    return false;
                }
                _phoneBindingNoticeUntil = DateTime.MinValue;
                SetupBackend setup = new SetupBackend(_root);
                setup.EnsureCanConfigure();
                _timer.Stop();
                using (SetupForm dialog = new SetupForm(setup))
                {
                    dialog.Icon = Icon;
                    dialog.ShowDialog(this);
                    _configurationMissing = !SetupBackend.IsConfigurationComplete(_root);
                    if (dialog.ConfigurationSaved)
                    {
                        AppendLog(DateTime.Now, "手机设置", "手机配置已保存，点击“开始连接”即可启动共享。", "Success");
                        return true;
                    }
                }
            }
            catch (Exception exception) { ShowPhoneBindingNotice(exception.Message); }
            finally
            {
                _operations.CompleteConfiguration();
                if (!_closing && !IsDisposed) { _timer.Start(); UpdateButtons(); PollOnce(); }
            }
            return false;
        }

        private bool PhoneBindingHasSession()
        {
            return (_snapshot != null && _snapshot.HasSession) || SetupBackend.HasSessionRecord(_root);
        }

        private void ShowPhoneBindingNotice(string message)
        {
            if (_closing || IsDisposed) return;
            RestoreFromTray();
            _phoneBindingNotice = message;
            _phoneBindingNoticeUntil = DateTime.UtcNow.AddSeconds(6);
            _status.Text = message.StartsWith("请先断开当前共享", StringComparison.Ordinal) ? "请先断开当前共享" : "手机设置暂不可用";
            _detail.Text = message;
            _detail.ForeColor = Color.FromArgb(255, 193, 99);
            AppendLog(DateTime.Now, "手机设置", message, "Info");
        }

        private void UpdateConnections(List<ConnectionInfo> entries)
        {
            int count = entries == null ? 0 : entries.Count;
            string tabTitle = "TCP 连接  " + count.ToString(CultureInfo.InvariantCulture);
            if (_connectionsTab.Text != tabTitle) _connectionsTab.Text = tabTitle;
            if (_logsVisible || !Visible || WindowState == FormWindowState.Minimized) return;
            StringBuilder signature = new StringBuilder();
            if (entries != null)
                foreach (ConnectionInfo entry in entries)
                    signature.Append(entry.Protocol).Append('|').Append(entry.RemoteAddress).Append('|').Append(entry.RemotePort).Append('|').Append(entry.State).Append('\n');
            string nextSignature = signature.ToString();
            if (String.Equals(_connectionsSignature, nextSignature, StringComparison.Ordinal)) return;
            _connectionsSignature = nextSignature;
            int selected = _connections.CurrentCell == null ? -1 : _connections.CurrentCell.RowIndex;
            int first = _connections.FirstDisplayedScrollingRowIndex;
            _connections.SuspendLayout();
            try
            {
                _connections.Rows.Clear();
                if (entries != null)
                {
                    foreach (ConnectionInfo entry in entries)
                    {
                        _connections.Rows.Add(entry.Protocol, entry.RemoteAddress, entry.RemotePort.ToString(CultureInfo.InvariantCulture), entry.State);
                    }
                }
                if (_connections.Rows.Count > 0)
                {
                    _connections.CurrentCell = null;
                    if (selected >= 0 && selected < _connections.Rows.Count) _connections.Rows[selected].Selected = true;
                    if (first >= 0 && first < _connections.Rows.Count) _connections.FirstDisplayedScrollingRowIndex = first;
                }
            }
            finally { _connections.ResumeLayout(); }
        }

        private void AppendLog(DateTime time, string source, string message, string level)
        {
            // May be called by process-output workers: no Control or native UI access here.
            if (String.IsNullOrWhiteSpace(message) || _closing) return;
            PendingLog entry = CreatePendingLog(time, source, message, level);
            lock (_pendingLogGate)
            {
                if (_closing || _disposedUiResources != 0) return;
                _pendingLogs.Enqueue(entry);
                _pendingLogCharacters += entry.Characters;
                while (_pendingLogs.Count > MaximumPendingLogEntries || _pendingLogCharacters > MaximumPendingLogCharacters)
                {
                    _pendingLogCharacters -= _pendingLogs.Dequeue().Characters;
                    if (_droppedPendingLogCount < Int32.MaxValue) _droppedPendingLogCount++;
                    if (_pendingDroppedToReport < Int32.MaxValue) _pendingDroppedToReport++;
                }
            }
        }

        private static PendingLog CreatePendingLog(DateTime time, string source, string message, string level)
        {
            string sourceName = String.IsNullOrWhiteSpace(source) ? "日志" : source;
            if (sourceName.Length > 40) sourceName = sourceName.Substring(0, 40);
            sourceName = sourceName.Replace('\r', ' ').Replace('\n', ' ');
            string timePrefix = time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ";
            string sourcePrefix = "[" + sourceName + "]  ";
            string body = LimitConsoleMessage(message, MaximumConsoleEntryCharacters - timePrefix.Length - sourcePrefix.Length - 1);
            string rendered = timePrefix + sourcePrefix + body + "\n";
            string normalized = level ?? String.Empty;
            if (normalized.Length > 64) normalized = normalized.Substring(0, 64);
            normalized = normalized.ToLowerInvariant();
            int color = normalized.Contains("error") || normalized.Contains("fatal") ? 4 : normalized.Contains("warn") ? 5 : normalized.Contains("success") ? 6 : 3;
            return new PendingLog { TimePrefix = timePrefix, SourcePrefix = sourcePrefix, Body = body,
                Characters = rendered.Length, LineLengths = GetConsoleLineLengths(rendered), ColorIndex = color };
        }

        // UI-thread timer entry point. Hidden controls do no RichEdit rendering work.
        internal void FlushPendingLogs()
        {
            FlushPendingLogsCore(false);
        }

        // An isolated harness can measure one actual batch without depending on window visibility.
        internal void FlushPendingLogsForTest()
        {
            FlushPendingLogsCore(true);
        }

        private void FlushPendingLogsCore(bool force)
        {
            if (_closing || IsDisposed || _flushingLogs) return;
            if (!force && (!Visible || WindowState == FormWindowState.Minimized || !_logsVisible || !_console.Visible)) return;
            if (InvokeRequired) throw new InvalidOperationException("Log flushing must run on the UI thread.");
            List<PendingLog> batch = new List<PendingLog>(MaximumFlushEntries);
            int batchCharacters = 0, batchLines = 0;
            lock (_pendingLogGate)
            {
                if (_pendingDroppedToReport > 0)
                {
                    PendingLog notice = CreatePendingLog(DateTime.Now, "显示", "刷新较快，已略过 " + _pendingDroppedToReport.ToString(CultureInfo.InvariantCulture) + " 条较早消息，仅显示最近内容。", "Warning");
                    batch.Add(notice); batchCharacters = notice.Characters; batchLines = notice.LineLengths.Count;
                    _pendingDroppedToReport = 0;
                }
                while (_pendingLogs.Count > 0 && batch.Count < MaximumFlushEntries)
                {
                    PendingLog next = _pendingLogs.Peek();
                    if (batchCharacters + next.Characters > MaximumFlushCharacters || batchLines + next.LineLengths.Count + 1 > MaximumConsoleLines) break;
                    _pendingLogs.Dequeue(); _pendingLogCharacters -= next.Characters;
                    batch.Add(next); batchCharacters += next.Characters; batchLines += next.LineLengths.Count;
                }
            }
            if (batch.Count == 0) return;
            _flushingLogs = true;
            try { RenderLogBatch(batch, batchCharacters, batchLines); }
            finally { _flushingLogs = false; }
        }

        private void RenderLogBatch(List<PendingLog> batch, int batchCharacters, int batchLines)
        {
            if (_consoleCharacterCount != _console.TextLength) RebuildConsoleAccounting();
            int selectionStart = _console.SelectionStart;
            int selectionLength = _console.SelectionLength;
            bool autoScroll = _autoScroll.Checked;
            NativePoint oldScroll = new NativePoint();
            int anchorCharacter = 0;
            Point anchorPosition = Point.Empty;
            if (!autoScroll)
            {
                SendPointMessage(_console.Handle, 0x04DD, IntPtr.Zero, ref oldScroll); // EM_GETSCROLLPOS
                anchorCharacter = _console.GetCharIndexFromPosition(new Point(1, 1));
                anchorPosition = _console.GetPositionFromCharIndex(anchorCharacter);
            }
            int removedCharacters = 0;
            int remainingCharacters = _consoleCharacterCount;
            while (_consoleLineLengths.Count > 0 &&
                (remainingCharacters + batchCharacters > MaximumConsoleCharacters ||
                 _consoleLineLengths.Count + batchLines + 1 > MaximumConsoleLines))
            {
                int oldLineLength = _consoleLineLengths.Dequeue();
                remainingCharacters -= oldLineLength;
                removedCharacters += oldLineLength;
            }
            bool wasReadOnly = _console.ReadOnly;
            bool redrawSuppressed = _console.Visible && _console.IsHandleCreated;
            if (redrawSuppressed) SendWindowMessage(_console.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW
            try
            {
                if (removedCharacters > 0)
                {
                    // WM_CLEAR requires a writable control. Always restore its read-only state.
                    _console.ReadOnly = false;
                    _console.Select(0, removedCharacters);
                    _console.SelectedText = String.Empty;
                    _console.ReadOnly = wasReadOnly;
                    int selectionEnd = Math.Max(0, selectionStart + selectionLength - removedCharacters);
                    selectionStart = Math.Max(0, selectionStart - removedCharacters);
                    selectionLength = Math.Max(0, selectionEnd - selectionStart);
                }
                _console.Select(_console.TextLength, 0);
                _console.SelectedRtf = BuildBatchRtf(batch);
                foreach (PendingLog entry in batch)
                    foreach (int length in entry.LineLengths) _consoleLineLengths.Enqueue(length);
                _consoleCharacterCount = _console.TextLength;
                _consoleLineCount = _consoleLineLengths.Count == 0 ? 0 : _consoleLineLengths.Count + 1;
                if (autoScroll)
                {
                    _console.Select(_console.TextLength, 0);
                    _console.ScrollToCaret();
                }
                else
                {
                    selectionStart = Math.Min(selectionStart, _console.TextLength);
                    _console.Select(selectionStart, Math.Min(selectionLength, _console.TextLength - selectionStart));
                    NativePoint restore = oldScroll;
                    if (removedCharacters > 0)
                    {
                        if (anchorCharacter < removedCharacters) restore.Y = 0;
                        else
                        {
                            NativePoint current = new NativePoint();
                            SendPointMessage(_console.Handle, 0x04DD, IntPtr.Zero, ref current);
                            Point nextAnchor = _console.GetPositionFromCharIndex(Math.Min(_console.TextLength, anchorCharacter - removedCharacters));
                            restore.Y = Math.Max(0, current.Y + nextAnchor.Y - anchorPosition.Y);
                        }
                    }
                    SendPointMessage(_console.Handle, 0x04DE, IntPtr.Zero, ref restore); // EM_SETSCROLLPOS
                }
            }
            finally
            {
                _console.ReadOnly = wasReadOnly;
                _console.ClearUndo();
                if (redrawSuppressed)
                {
                    SendWindowMessage(_console.Handle, 0x000B, new IntPtr(1), IntPtr.Zero);
                    _console.Invalidate();
                }
            }
        }

        private static string BuildBatchRtf(List<PendingLog> batch)
        {
            StringBuilder rtf = new StringBuilder(16384);
            rtf.Append(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0\fmodern Consolas;}}{\colortbl;\red146\green166\blue186;\red113\green161\blue255;\red185\green205\blue221;\red255\green133\blue135;\red255\green200\blue103;\red48\green216\blue192;}\f0\fs18 ");
            foreach (PendingLog entry in batch)
            {
                rtf.Append(@"\cf1 "); AppendRtfText(rtf, entry.TimePrefix);
                rtf.Append(@"\cf2 "); AppendRtfText(rtf, entry.SourcePrefix);
                rtf.Append(@"\cf").Append(entry.ColorIndex).Append(' '); AppendRtfText(rtf, entry.Body);
                rtf.Append(@"\par ");
            }
            return rtf.Append('}').ToString();
        }

        private static void AppendRtfText(StringBuilder rtf, string text)
        {
            foreach (char character in text)
            {
                if (character == '\\' || character == '{' || character == '}') rtf.Append('\\').Append(character);
                else if (character == '\n') rtf.Append(@"\par ");
                else if (character == '\t') rtf.Append(@"\tab ");
                else if (character < 32) rtf.Append(' ');
                else if (character > 126) rtf.Append(@"\u").Append(unchecked((short)character)).Append('?');
                else rtf.Append(character);
            }
        }

        private static string LimitConsoleMessage(string message, int maximumLength)
        {
            bool truncated = message.Length > maximumLength;
            string text = (truncated ? message.Substring(0, maximumLength) : message).Replace("\r\n", "\n").Replace('\r', '\n');
            int breaks = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' && ++breaks >= MaximumConsoleLines - 1)
                {
                    text = text.Substring(0, i);
                    truncated = true;
                    break;
                }
            }
            if (!truncated) return text;
            const string marker = " … [已截断]";
            int keep = Math.Min(text.Length, Math.Max(0, maximumLength - marker.Length));
            if (keep > 0 && Char.IsHighSurrogate(text[keep - 1])) keep--;
            return text.Substring(0, keep) + marker;
        }

        private static List<int> GetConsoleLineLengths(string text)
        {
            List<int> lengths = new List<int>();
            int lineStart = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') { lengths.Add(i - lineStart + 1); lineStart = i + 1; }
            }
            if (lineStart < text.Length) lengths.Add(text.Length - lineStart);
            return lengths;
        }

        private void RebuildConsoleAccounting()
        {
            _consoleLineLengths.Clear();
            foreach (int length in GetConsoleLineLengths(_console.Text)) _consoleLineLengths.Enqueue(length);
            _consoleCharacterCount = _console.TextLength;
            _consoleLineCount = _consoleLineLengths.Count == 0 ? 0 : _consoleLineLengths.Count + 1;
        }

        private void ClearConsoleDisplay()
        {
            lock (_pendingLogGate)
            {
                _pendingLogs.Clear();
                _pendingLogCharacters = 0;
                _droppedPendingLogCount = 0;
                _pendingDroppedToReport = 0;
            }
            bool wasReadOnly = _console.ReadOnly;
            try { _console.ReadOnly = false; _console.Clear(); _console.ClearUndo(); }
            finally { _console.ReadOnly = wasReadOnly; }
            _consoleLineLengths.Clear();
            _consoleLineCount = 0;
            _consoleCharacterCount = 0;
            _seenLogs.Clear();
            _seenLogOrder.Clear();
            if (_snapshot != null && _snapshot.Logs != null) _snapshot.Logs.Clear();
        }

        private void SetLogView(bool showLogs)
        {
            _logsVisible = showLogs;
            _console.Visible = showLogs;
            _connections.Visible = !showLogs;
            if (showLogs) _console.BringToFront(); else _connections.BringToFront();
            StyleTab(_logsTab, showLogs);
            StyleTab(_connectionsTab, !showLogs);
            if (showLogs) FlushPendingLogs();
            else if (_snapshot != null) UpdateConnections(_snapshot.Connections);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (!_allowClose && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing)
            {
                e.Cancel = true;
                ExitApplication();
                return;
            }
            // Windows may terminate us during shutdown. Do not hide or indefinitely veto shutdown;
            // the existing phone-local timeout remains independent of this GUI.
            if (!_allowClose && !_preview && !_actionBusy) StartShutdownStop();
            _operations.BeginClosing();
            _timer.Stop();
            _logTimer.Stop();
        }

        private bool HasSessionOnDisk()
        {
            // Serialize with connection helpers, including the interval before their first state write.
            using (Mutex mutex = new Mutex(false, "Local\\CodexUsbInternetWireless"))
            {
                bool locked = false;
                try
                {
                    try { locked = mutex.WaitOne(6000); } catch (AbandonedMutexException) { locked = true; }
                    if (!locked) throw new IOException("另一个连接操作仍在执行，请稍后重试退出。");
                    // File.Exists hides access errors; those must prevent a falsely successful normal exit.
                    try { File.GetAttributes(Path.Combine(_root, "state", "wireless-session.json")); return true; }
                    catch (FileNotFoundException) { return false; }
                    catch (DirectoryNotFoundException) { return false; }
                }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }

        private void StartShutdownStop()
        {
            // Start synchronously so Windows cannot end the UI before a queued thread gets to run.
            // Do not wait or veto system shutdown; the phone-local guard also survives GUI termination.
            try
            {
                File.GetAttributes(Path.Combine(_root, "state", "wireless-session.json"));
                string worker = Path.Combine(_root, "support", "ConnectionWorker.exe");
                using (Process child = Process.Start(new ProcessStartInfo(worker, "--action Stop --root " + Backend.Quote(_root)) {
                    WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true })) { }
            }
            catch { }
        }

        private void HideToTray()
        {
            if (_closing) return;
            Hide();
            ShowInTaskbar = false;
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloonTip(2500, "无线网络共享已收起", "双击托盘图标可恢复窗口；右键“退出”会自动断开共享。", ToolTipIcon.Info);
            }
        }

        internal void RestoreFromTray()
        {
            if (_closing || IsDisposed) return;
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            if (_logsVisible) FlushPendingLogs();
            else if (_snapshot != null) UpdateConnections(_snapshot.Connections);
        }

        internal async void ExitApplication()
        {
            if (!_operations.TryRequestExit()) return;
            UpdateButtons();
            _status.Text = "正在断开并退出…";
            _detail.Text = "确认本次共享已结束后，程序将自动退出。";
            AppendLog(DateTime.Now, "退出", "已请求退出，将先结束本次共享。", "Info");
            try
            {
                // A Start already in flight must finish before Stop; new actions/polls are blocked.
                await _operations.WaitForActionAsync();
                await _operations.WaitForPollAsync();
                if (!_preview && await Task.Run(delegate { return HasSessionOnDisk(); }))
                {
                    int stopped = await RunActionAsync("Stop", true);
                    if (stopped != 0 || await Task.Run(delegate { return HasSessionOnDisk(); })) throw new IOException("尚未确认共享已断开，请查看连接日志后重试退出。");
                }
                _operations.PermitClose();
                Close();
            }
            catch (Exception ex)
            {
                if (_closing || IsDisposed) return;
                _operations.CancelExit();
                AppendLog(DateTime.Now, "退出", "退出未完成：" + ex.Message + " 程序继续运行。", "Error");
                RestoreFromTray();
                _status.Text = "退出未完成";
                _detail.Text = ex.Message;
                _trayIcon.ShowBalloonTip(4000, "未能确认共享已断开", "程序继续运行，请查看主窗口日志并重试退出。", ToolTipIcon.Warning);
                UpdateButtons();
                PollOnce();
            }
        }

        // Preserve the pre-existing developer capture mode: it closes only its temporary observer.
        // Ordinary user exit is exclusively ExitApplication(), which first stops sharing.
        internal void CloseObserverAfterCapture()
        {
            _operations.PermitClose();
            Close();
        }

        private void DisposeBackend()
        {
            if (Interlocked.Exchange(ref _disposedBackend, 1) == 0)
            {
                try { _backend.Dispose(); } catch { }
            }
        }

        private void ReleaseUiResources()
        {
            if (Interlocked.Exchange(ref _disposedUiResources, 1) != 0) return;
            _operations.BeginClosing();
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); }
            if (_logTimer != null) { _logTimer.Stop(); _logTimer.Dispose(); }
            lock (_pendingLogGate)
            {
                _pendingLogs.Clear();
                _pendingLogCharacters = 0;
                _pendingDroppedToReport = 0;
            }
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
            if (_trayMenu != null) _trayMenu.Dispose();
            Task pending = _pollTask;
            if (pending != null && !pending.IsCompleted) pending.ContinueWith(delegate { DisposeBackend(); });
            else DisposeBackend();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseUiResources();
            base.Dispose(disposing);
        }

        internal new void Capture(string path)
        {
            string method = "DrawToBitmap";
            using (Bitmap bitmap = new Bitmap(Width, Height))
            {
                bool printed = false;
                if (IsHandleCreated)
                {
                    // Capture only this program's own window; no screen or other application is read.
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        IntPtr deviceContext = graphics.GetHdc();
                        try { printed = PrintWindow(Handle, deviceContext, 2); }
                        catch (DllNotFoundException) { }
                        catch (EntryPointNotFoundException) { }
                        finally { graphics.ReleaseHdc(deviceContext); }
                    }
                }
                if (printed && IsBlankCapture(bitmap)) printed = false;
                if (printed) method = "PrintWindow";
                else DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                bitmap.Save(path, ImageFormat.Png);
            }
            string[] lines = _console.Lines;
            string first = String.Empty;
            string last = String.Empty;
            for (int i = 0; i < lines.Length; i++) if (!String.IsNullOrWhiteSpace(lines[i])) { first = lines[i]; break; }
            for (int i = lines.Length - 1; i >= 0; i--) if (!String.IsNullOrWhiteSpace(lines[i])) { last = lines[i]; break; }
            string evidence = "{\n" +
                "  \"CaptureMethod\": " + JsonString(method) + ",\n" +
                "  \"Preview\": " + (_preview ? "true" : "false") + ",\n" +
                "  \"ConsoleTextLength\": " + _console.TextLength.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"ConsoleCharacterCount\": " + _consoleCharacterCount.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"ConsoleLineCount\": " + _consoleLineCount.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"ConsoleDedupCount\": " + _seenLogs.Count.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"PendingLogCount\": " + PendingLogCount.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"PendingLogCharacters\": " + PendingLogCharacters.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"DroppedPendingLogCount\": " + DroppedPendingLogCount.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"ConsoleVisible\": " + (_console.Visible ? "true" : "false") + ",\n" +
                "  \"ConsoleBounds\": " + JsonString(_console.Bounds.ToString()) + ",\n" +
                "  \"ConsoleParentBounds\": " + JsonString(_console.Parent.Bounds.ToString()) + ",\n" +
                "  \"ConsoleFirstLine\": " + JsonString(first) + ",\n" +
                "  \"ConsoleLastLine\": " + JsonString(last) + ",\n" +
                "  \"StatusText\": " + JsonString(_status.Text) + ",\n" +
                "  \"DetailText\": " + JsonString(_detail.Text) + ",\n" +
                "  \"StartButtonEnabled\": " + (_startButton.Enabled ? "true" : "false") + ",\n" +
                "  \"StopButtonEnabled\": " + (_stopButton.Enabled ? "true" : "false") + ",\n" +
                "  \"ActionBusy\": " + (_actionBusy ? "true" : "false") + ",\n" +
                "  \"LogTabSelected\": " + (_logsVisible ? "true" : "false") + "\n}";
            File.WriteAllText(path + ".ui.json", evidence, new UTF8Encoding(false));
        }

        private static bool IsBlankCapture(Bitmap bitmap)
        {
            // Some desktop/session combinations return success with an empty surface.
            // Sample this bitmap only; never inspect the screen to validate a capture.
            int black = 0;
            int count = 0;
            int minimumRed = 255, minimumGreen = 255, minimumBlue = 255;
            int maximumRed = 0, maximumGreen = 0, maximumBlue = 0;
            for (int row = 0; row < 35; row++)
            {
                int y = Math.Min(bitmap.Height - 1, (int)((row + .5) * bitmap.Height / 35));
                for (int column = 0; column < 45; column++)
                {
                    int x = Math.Min(bitmap.Width - 1, (int)((column + .5) * bitmap.Width / 45));
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R <= 3 && pixel.G <= 3 && pixel.B <= 3) black++;
                    minimumRed = Math.Min(minimumRed, pixel.R);
                    minimumGreen = Math.Min(minimumGreen, pixel.G);
                    minimumBlue = Math.Min(minimumBlue, pixel.B);
                    maximumRed = Math.Max(maximumRed, pixel.R);
                    maximumGreen = Math.Max(maximumGreen, pixel.G);
                    maximumBlue = Math.Max(maximumBlue, pixel.B);
                    count++;
                }
            }
            return black >= count * .98 ||
                (maximumRed - minimumRed <= 2 && maximumGreen - minimumGreen <= 2 && maximumBlue - minimumBlue <= 2);
        }

        private static string JsonString(string value)
        {
            StringBuilder text = new StringBuilder("\"");
            foreach (char character in value ?? String.Empty)
            {
                if (character == '"') text.Append("\\\"");
                else if (character == '\\') text.Append("\\\\");
                else if (character == '\n') text.Append("\\n");
                else if (character == '\r') text.Append("\\r");
                else if (character == '\t') text.Append("\\t");
                else if (character < 32) text.Append("\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(character);
            }
            return text.Append('"').ToString();
        }

        private static Label MakeLabel(string text, float size, Color color, FontStyle style)
        {
            return new Label { Text = text, ForeColor = color, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", size, style), AutoSize = false };
        }

        private static Button MakeButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = BorderColor;
            button.BackColor = primary ? AccentColor : Color.FromArgb(27, 41, 58);
            button.ForeColor = primary ? Color.FromArgb(6, 39, 38) : TextColor;
            button.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(92, 232, 212) : Color.FromArgb(38, 55, 73);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(25, 185, 165) : Color.FromArgb(44, 65, 85);
            return button;
        }

        private static Button MakeTabButton(string text, bool selected)
        {
            Button button = MakeSmallButton(text);
            button.Width = 123;
            button.Margin = new Padding(0, 0, 7, 0);
            button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            StyleTab(button, selected);
            return button;
        }

        private static void StyleTab(Button button, bool selected)
        {
            button.BackColor = selected ? Color.FromArgb(31, 68, 70) : PanelColor;
            button.ForeColor = selected ? AccentColor : MutedColor;
            button.FlatAppearance.BorderColor = selected ? Color.FromArgb(41, 95, 94) : PanelColor;
        }

        private static Button MakeSmallButton(string text)
        {
            Button button = MakeButton(text, false);
            button.Height = 31;
            button.Font = new Font("Microsoft YaHei UI", 8.5F);
            button.Margin = new Padding(5, 0, 0, 0);
            return button;
        }

        private static DataGridView MakeConnectionGrid()
        {
            DataGridView grid = new DataGridView();
            grid.BackgroundColor = PanelColor;
            grid.BorderStyle = BorderStyle.None;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoGenerateColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = BorderColor;
            grid.ColumnHeadersHeight = 31;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(26, 40, 56), ForeColor = MutedColor, Font = new Font("Microsoft YaHei UI", 8.5F), Padding = new Padding(8, 0, 0, 0), SelectionBackColor = Color.FromArgb(26, 40, 56) };
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = PanelColor, ForeColor = TextColor, SelectionBackColor = Color.FromArgb(34, 65, 80), SelectionForeColor = TextColor, Padding = new Padding(8, 0, 0, 0), Font = new Font("Consolas", 9F) };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(22, 34, 48);
            grid.RowTemplate.Height = 29;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Protocol", HeaderText = "协议", Width = 95, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RemoteAddress", HeaderText = "远端 IP 地址", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RemotePort", HeaderText = "端口", Width = 110, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "状态", Width = 160, SortMode = DataGridViewColumnSortMode.NotSortable });
            return grid;
        }

        private class RoundedPanel : Panel
        {
            public RoundedPanel() { DoubleBuffered = true; BackColor = PanelColor; }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(BackgroundColor);
                if (Width < 2 || Height < 2) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = UiDpi.For(this);
                using (GraphicsPath path = RoundRect(new RectangleF(.5F * scale, .5F * scale, Width - scale, Height - scale), 10F * scale))
                using (SolidBrush fill = new SolidBrush(PanelColor))
                using (Pen border = new Pen(BorderColor, scale))
                { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path); }
            }
        }

        private static GraphicsPath RoundRect(RectangleF rectangle, float radius)
        {
            float diameter = radius * 2;
            GraphicsPath result = new GraphicsPath();
            result.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            result.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            result.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            result.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            result.CloseFigure();
            return result;
        }

        private sealed class MetricCard : RoundedPanel
        {
            private readonly Label _value;
            private readonly Label _caption;
            public MetricCard(string title, string value, string caption, Color color)
            {
                Padding = new Padding(16, 11, 12, 8);
                Label heading = MakeLabel(title, 8.5F, MutedColor, FontStyle.Regular);
                heading.Dock = DockStyle.Top;
                heading.Height = 22;
                _caption = MakeLabel(caption, 8F, MutedColor, FontStyle.Regular);
                _caption.Dock = DockStyle.Bottom;
                _caption.Height = 21;
                _caption.AutoEllipsis = true;
                _value = MakeLabel(value, 22F, color, FontStyle.Bold);
                _value.Dock = DockStyle.Fill;
                _value.TextAlign = ContentAlignment.MiddleLeft;
                Controls.Add(_value);
                Controls.Add(_caption);
                Controls.Add(heading);
            }
            public void SetValue(string value, string caption) { _value.Text = value; _caption.Text = caption; }
        }

        private sealed class StatusLight : Control
        {
            private Color _color = MutedColor;
            public Color LightColor { get { return _color; } set { _color = value; Invalidate(); } }
            public StatusLight() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); BackColor = Color.Transparent; }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float inset = UiDpi.For(this);
                using (SolidBrush brush = new SolidBrush(_color)) e.Graphics.FillEllipse(brush, inset, inset, Width - 2 * inset, Height - 2 * inset);
            }
        }

        private sealed class LogoMark : Control
        {
            public LogoMark() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.ScaleTransform(Width / 43F, Height / 43F);
                using (GraphicsPath shape = RoundRect(new RectangleF(0, 0, 42, 42), 10))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(22, 66, 65))) e.Graphics.FillPath(fill, shape);
                using (SolidBrush bars = new SolidBrush(AccentColor))
                {
                    e.Graphics.FillRectangle(bars, 10, 25, 4, 8);
                    e.Graphics.FillRectangle(bars, 19, 18, 4, 15);
                    e.Graphics.FillRectangle(bars, 28, 10, 4, 23);
                }
            }
        }

        private sealed class SpeedChart : Control
        {
            private readonly List<double> _download = new List<double>();
            private readonly List<double> _upload = new List<double>();
            public SpeedChart() { DoubleBuffered = true; BackColor = PanelColor; SetStyle(ControlStyles.ResizeRedraw, true); }
            public void AddSample(double download, double upload)
            {
                _download.Add(SafeValue(download));
                _upload.Add(SafeValue(upload));
                while (_download.Count > 60) { _download.RemoveAt(0); _upload.RemoveAt(0); }
                Invalidate();
            }
            public void ClearSamples() { _download.Clear(); _upload.Clear(); Invalidate(); }
            private static double SafeValue(double value) { return Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 ? Double.NaN : value; }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                float scale = UiDpi.For(this);
                if (Width < 120 * scale || Height < 80 * scale) return;
                Graphics graphics = e.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Font heading = new Font("Microsoft YaHei UI", 12F * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font small = new Font("Microsoft YaHei UI", (32F / 3F) * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush text = new SolidBrush(TextColor))
                using (SolidBrush muted = new SolidBrush(MutedColor))
                {
                    graphics.DrawString("实时网速", heading, text, 0, 0);
                    graphics.DrawString("最近 60 次采样", small, muted, 85 * scale, 2 * scale);
                    DrawLegend(graphics, "下载", AccentColor, Width - 151 * scale, 8 * scale, small, scale);
                    DrawLegend(graphics, "上传", UploadColor, Width - 75 * scale, 8 * scale, small, scale);
                    double maximum = 1024;
                    for (int i = 0; i < _download.Count; i++)
                    {
                        if (!Double.IsNaN(_download[i])) maximum = Math.Max(maximum, _download[i]);
                        if (!Double.IsNaN(_upload[i])) maximum = Math.Max(maximum, _upload[i]);
                    }
                    maximum *= 1.15;
                    float axisWidth = 77 * scale;
                    for (int row = 0; row <= 2; row++)
                        axisWidth = Math.Max(axisWidth, graphics.MeasureString(FormatRate(maximum * (1 - row / 2.0)), small).Width + 14 * scale);
                    RectangleF plot = new RectangleF(scale, 31 * scale, Width - axisWidth, Height - 51 * scale);
                    using (Pen grid = new Pen(Color.FromArgb(36, 54, 71), scale))
                    {
                        for (int row = 0; row <= 2; row++)
                        {
                            float y = plot.Top + plot.Height * row / 2;
                            graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
                            graphics.DrawString(FormatRate(maximum * (1 - row / 2.0)), small, muted, plot.Right + 9 * scale, y - 8 * scale);
                        }
                    }
                    DrawSeries(graphics, plot, _download, maximum, AccentColor, true, scale);
                    DrawSeries(graphics, plot, _upload, maximum, UploadColor, false, scale);
                    graphics.DrawString("较早", small, muted, plot.Left, plot.Bottom + 4 * scale);
                    SizeF now = graphics.MeasureString("现在", small);
                    graphics.DrawString("现在", small, muted, plot.Right - now.Width, plot.Bottom + 4 * scale);
                }
            }
            private static void DrawLegend(Graphics graphics, string label, Color color, float x, float y, Font font, float scale)
            {
                using (Pen pen = new Pen(color, 2 * scale)) graphics.DrawLine(pen, x, y + 2 * scale, x + 17 * scale, y + 2 * scale);
                using (SolidBrush brush = new SolidBrush(MutedColor)) graphics.DrawString(label, font, brush, x + 23 * scale, y - 7 * scale);
            }
            private static void DrawSeries(Graphics graphics, RectangleF plot, List<double> values, double maximum, Color color, bool fill, float scale)
            {
                if (values.Count == 0) return;
                List<PointF> points = new List<PointF>();
                int offset = 60 - values.Count;
                for (int i = 0; i < values.Count; i++)
                {
                    if (Double.IsNaN(values[i]))
                    {
                        DrawSegment(graphics, plot, points.ToArray(), color, fill, scale);
                        points.Clear();
                    }
                    else points.Add(new PointF(plot.Left + plot.Width * (offset + i) / 59, plot.Bottom - (float)(plot.Height * values[i] / maximum)));
                }
                DrawSegment(graphics, plot, points.ToArray(), color, fill, scale);
            }
            private static void DrawSegment(Graphics graphics, RectangleF plot, PointF[] points, Color color, bool fill, float scale)
            {
                if (points.Length == 0) return;
                if (points.Length == 1)
                {
                    using (SolidBrush dot = new SolidBrush(color)) graphics.FillEllipse(dot, points[0].X - 2 * scale, points[0].Y - 2 * scale, 4 * scale, 4 * scale);
                    return;
                }
                if (fill)
                {
                    using (GraphicsPath area = new GraphicsPath())
                    {
                        area.AddLine(points[0].X, plot.Bottom, points[0].X, points[0].Y);
                        area.AddLines(points);
                        area.AddLine(points[points.Length - 1].X, points[points.Length - 1].Y, points[points.Length - 1].X, plot.Bottom);
                        area.CloseFigure();
                        using (LinearGradientBrush brush = new LinearGradientBrush(plot, Color.FromArgb(49, color), Color.FromArgb(3, color), LinearGradientMode.Vertical)) graphics.FillPath(brush, area);
                    }
                }
                using (Pen line = new Pen(color, 2F * scale)) { line.LineJoin = LineJoin.Round; graphics.DrawLines(line, points); }
            }
        }
    }
}
