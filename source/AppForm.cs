using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
        private static readonly Color BackgroundColor = UiVisualStyle.BackgroundColor;
        private static readonly Color PanelColor = UiVisualStyle.PanelColor;
        private static readonly Color BorderColor = UiVisualStyle.BorderColor;
        private static readonly Color TextColor = UiVisualStyle.TextColor;
        private static readonly Color MutedColor = UiVisualStyle.MutedColor;
        private static readonly Color AccentColor = UiVisualStyle.AccentColor;
        private static readonly Color UploadColor = UiVisualStyle.UploadColor;
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
        private readonly ConsoleLogView _logView;
        // Read-only compatibility statistics; all mutable log state belongs to the view.
        private int _consoleLineCount { get { return _logView.ConsoleLineCount; } }
        private int _consoleCharacterCount { get { return _logView.ConsoleCharacterCount; } }
        private System.Collections.ICollection _seenLogOrder { get { return _logView.DedupOrderSnapshot; } }
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
        private string _connectionsSignature = String.Empty;
        private bool _refreshPhoneSample = true;
        private bool _phoneStatisticsVisible;

        internal int PendingLogCount { get { return _logView.PendingCount; } }
        internal int PendingLogCharacters { get { return _logView.PendingCharacters; } }
        internal int DroppedPendingLogCount { get { return _logView.DroppedCount; } }

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
            _logView = new ConsoleLogView(_console, _autoScroll,
                delegate { return _operations.IsClosing; },
                delegate { return !IsDisposed && Visible && WindowState != FormWindowState.Minimized && _logsVisible && _console.Visible; });
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
            VisibleChanged += delegate { UpdatePhoneSamplingVisibility(); };
            Resize += delegate { UpdatePhoneSamplingVisibility(); };
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

        private async void PollOnce()
        {
            if (!_operations.TryBeginPoll()) return;
            bool foreground = Visible && WindowState != FormWindowState.Minimized;
            PhoneSamplingMode mode = !foreground ? PhoneSamplingMode.Paused :
                _refreshPhoneSample ? PhoneSamplingMode.Refresh : PhoneSamplingMode.Foreground;
            // Consume refresh only after admission. Restoring during an in-flight
            // poll must leave it pending until a new foreground poll can start.
            if (foreground) _refreshPhoneSample = false;
            try
            {
                Task<Snapshot> task = Task.Run(delegate { return _backend.Poll(mode); });
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
            finally
            {
                _operations.CompletePoll();
                if (_refreshPhoneSample && Visible && WindowState != FormWindowState.Minimized && !_closing && !_exitRequested) PollOnce();
            }
        }

        private void UpdatePhoneSamplingVisibility()
        {
            bool foreground = Visible && WindowState != FormWindowState.Minimized;
            if (foreground == _phoneStatisticsVisible) return;
            _phoneStatisticsVisible = foreground;
            if (foreground) _refreshPhoneSample = true;
            if (IsHandleCreated && !_closing && !_exitRequested) PollOnce();
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
            if (value.HasSession && value.IsNewTelemetrySample) _chart.AddSample(telemetryCurrent ? value.DownloadBytesPerSecond : Double.NaN, telemetryCurrent ? value.UploadBytesPerSecond : Double.NaN);
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
                    _logView.AppendUnique(key, item.Time, item.Source, item.Message, item.Level);
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
        { return UiVisualStyle.FormatBytes(bytes); }

        private static string FormatRate(double bytes)
        { return UiVisualStyle.FormatRate(bytes); }

        private static string FormatSize(double bytes, bool rate)
        { return UiVisualStyle.FormatSize(bytes, rate); }

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
        { _logView.Append(time, source, message, level); }

        internal void FlushPendingLogs() { _logView.Flush(); }
        internal void FlushPendingLogsForTest() { _logView.FlushForTest(); }

        private void ClearConsoleDisplay()
        {
            _logView.Clear();
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
            if (_logView != null) _logView.Dispose();
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
                "  \"ConsoleDedupCount\": " + _logView.DedupCount.ToString(CultureInfo.InvariantCulture) + ",\n" +
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
        { return UiVisualStyle.MakeLabel(text, size, color, style); }

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

    }
}
