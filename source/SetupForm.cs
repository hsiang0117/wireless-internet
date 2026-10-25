using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WirelessInternet
{
    public sealed class SetupForm : Form
    {
        static readonly Color BackgroundColor = Color.FromArgb(12, 20, 31);
        static readonly Color SurfaceColor = Color.FromArgb(20, 31, 45);
        static readonly Color TextColor = Color.FromArgb(235, 243, 250);
        static readonly Color MutedColor = Color.FromArgb(146, 166, 186);
        static readonly Color AccentColor = Color.FromArgb(48, 216, 192);
        readonly SetupBackend backend;
        readonly TextBox _pairAddress, _pairCode, _connectAddress;
        readonly NumericUpDown _wifiIndex, _ethernetIndex;
        readonly Button _pairButton, _saveButton, _cancelButton;
        readonly Label _pairTarget, _connectTarget, _status, _bindingLabel;
        readonly ProgressBar _progress;
        bool busy;
        public bool ConfigurationSaved { get; private set; }

        public SetupForm(string root) : this(new SetupBackend(root)) { }
        public SetupForm(SetupBackend backend)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            this.backend = backend;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Text = "更换/绑定手机 · 无线网络共享";
            BackColor = BackgroundColor;
            ForeColor = TextColor;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(920, 820);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            TableLayoutPanel layout = Stack();
            layout.Dock = DockStyle.Fill;
            layout.AutoSize = false;
            layout.Padding = new Padding(24, 16, 24, 16);
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            TableLayoutPanel header = Stack();
            header.Margin = new Padding(0, 0, 0, 12);
            Label heading = Label("更换或绑定安卓手机", 19F, TextColor, true);
            AddRow(header, heading, 4);
            _bindingLabel = Label("当前绑定：正在读取…", 9F, AccentColor, true);
            AddRow(header, _bindingLabel, 6);
            Label lead = Label("更换时先断开旧共享，并关闭旧手机无线调试。已配对设备可直接检测保存。\n新手机验证保存成功才替换旧绑定；取消/失败保留原配置，不会删除旧手机系统配对记录。", 8.5F, MutedColor, false);
            AddRow(header, lead, 0);
            layout.Controls.Add(header, 0, 0);

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, Margin = new Padding(0, 12, 0, 0), Size = new Size(1, 1) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _cancelButton = Button("稍后配置", false);
            _cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _cancelButton.MinimumSize = new Size(118, 36);
            _cancelButton.Margin = new Padding(16, 0, 0, 0);
            _cancelButton.Click += delegate { if (!busy) { DialogResult = DialogResult.Cancel; Close(); } };
            _status = Label("配对码只在本次操作中使用，不写入文件或实时日志。", 9F, MutedColor, false);
            _status.Margin = new Padding(0, 3, 0, 3);
            _progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 5, Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 35 };
            footer.Controls.Add(_status, 0, 0);
            footer.Controls.Add(_cancelButton, 1, 0);
            footer.Controls.Add(_progress, 0, 1);
            footer.SetColumnSpan(_progress, 2);
            layout.Controls.Add(footer, 0, 2);

            Panel scroll = new Panel { Name = "SetupScroll", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            TableLayoutPanel content = Stack(); content.Name = "SetupContent";
            content.Dock = DockStyle.Top;
            scroll.Controls.Add(content); layout.Controls.Add(scroll, 0, 1);

            TableLayoutPanel preparation = Section(content);
            Label prepTitle = Label("先准备好手机与电脑", 11F, TextColor, true); AddRow(preparation, prepTitle, 7);
            Label prep = Label("① 手机连接 Wi-Fi，并开启热点；电脑连接这台手机的热点。\n② 手机进入“开发者选项 → 无线调试”，手动保持开关开启。\n③ 手机无需插 USB；配对页面和主页面的端口不同，请分别填写。", 9F, MutedColor, false);
            AddRow(preparation, prep, 0);

            TableLayoutPanel pairing = Section(content);
            Label pairTitle = Label("1 · 首次使用：配对", 11F, TextColor, true); AddRow(pairing, pairTitle, 7);
            TableLayoutPanel pairRows = Rows(4, 4);
            pairRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            pairRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            pairRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pairRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            pairRows.Controls.Add(RowLabel("配对地址 / 端口"), 0, 0);
            _pairAddress = Input(); pairRows.Controls.Add(_pairAddress, 1, 0);
            pairRows.SetColumnSpan(_pairAddress, 3);
            pairRows.Controls.Add(RowLabel("配对码"), 0, 1);
            _pairCode = Input(); _pairCode.MaxLength = 6; _pairCode.UseSystemPasswordChar = true; pairRows.Controls.Add(_pairCode, 1, 1);
            _pairButton = Button("开始配对", false); _pairButton.Anchor = AnchorStyles.Left | AnchorStyles.Right; pairRows.Controls.Add(_pairButton, 3, 1);
            _pairTarget = Label("实际地址：填写端口后显示手机热点目标。", 8F, MutedColor, false);
            pairRows.Controls.Add(_pairTarget, 0, 2); pairRows.SetColumnSpan(_pairTarget, 4);
            Label pairHelp = Label("在手机选择“使用配对码配对设备”。可只填端口，也可粘贴页面上的 IP:端口。配对码会立即清空，失败时请读取手机上的新码。", 8.5F, MutedColor, false);
            pairRows.Controls.Add(pairHelp, 0, 3); pairRows.SetColumnSpan(pairHelp, 4);
            AddRow(pairing, pairRows, 0);

            TableLayoutPanel connection = Section(content);
            Label connectTitle = Label("2 · 检测连接并保存", 11F, TextColor, true); AddRow(connection, connectTitle, 7);
            TableLayoutPanel connectRows = Rows(3, 3);
            connectRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); connectRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); connectRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            connectRows.Controls.Add(RowLabel("连接地址 / 端口"), 0, 0);
            _connectAddress = Input(); connectRows.Controls.Add(_connectAddress, 1, 0);
            _saveButton = Button("已配对，检测并保存", true); _saveButton.Anchor = AnchorStyles.Left | AnchorStyles.Right; connectRows.Controls.Add(_saveButton, 2, 0);
            _connectTarget = Label("实际地址：使用手机热点网关与连接端口。", 8F, MutedColor, false); connectRows.Controls.Add(_connectTarget, 0, 1); connectRows.SetColumnSpan(_connectTarget, 3);
            Label connectHelp = Label("返回“无线调试”主页面，填写“IP 地址和端口”。程序通过电脑所连热点的网关访问同一个端口，手机页面上的 Wi-Fi IP 可能与实际地址不同。", 8.5F, MutedColor, false); connectRows.Controls.Add(connectHelp, 0, 2); connectRows.SetColumnSpan(connectHelp, 3);
            AddRow(connection, connectRows, 0);

            TableLayoutPanel advanced = Section(content);
            Label advancedTitle = Label("高级：网卡索引（0 = 自动识别，通常无需修改）", 9F, MutedColor, false); AddRow(advanced, advancedTitle, 7);
            FlowLayoutPanel indices = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty, Size = new Size(1, 1) };
            indices.Controls.Add(new Label { Text = "Wi-Fi", Width = 49, Height = 29, ForeColor = MutedColor, TextAlign = ContentAlignment.MiddleLeft });
            _wifiIndex = IndexInput(); indices.Controls.Add(_wifiIndex);
            indices.Controls.Add(new Label { Text = "有线网络", Width = 92, Height = 29, ForeColor = MutedColor, TextAlign = ContentAlignment.MiddleRight });
            _ethernetIndex = IndexInput(); indices.Controls.Add(_ethernetIndex);
            AddRow(advanced, indices, 0);

            TableLayoutPanel compatibility = Section(content);
            Label limitations = Label("兼容范围：Windows 10/11 x64、Android 11+、机主用户（user 0）。手机必须支持 Wi-Fi 与热点同时开启，且原生无线调试能保持开启；部分厂商或网络环境可能不支持。\n此向导不会修改系统开关、路由或校园认证。首次共享时请在手机上允许 gnirehtet 安装 / VPN 请求；两个 VPN 限制开关保持关闭。", 8F, MutedColor, false);
            AddRow(compatibility, limitations, 0);

            _pairAddress.TextChanged += delegate { UpdateTarget(_pairAddress, _pairTarget); };
            _connectAddress.TextChanged += delegate { UpdateTarget(_connectAddress, _connectTarget); };
            _wifiIndex.ValueChanged += delegate { UpdateTarget(_pairAddress, _pairTarget); UpdateTarget(_connectAddress, _connectTarget); };
            _pairButton.Click += PairClicked;
            _saveButton.Click += SaveClicked;
            FormClosing += delegate(object sender, FormClosingEventArgs args) { if (busy) { args.Cancel = true; _status.Text = "操作正在进行，请等待完成后再关闭。"; } };
            Shown += delegate
            {
                UiDpi.ClampToWorkingArea(this, new Size(700, 540));
                try
                {
                    backend.EnsureCanConfigure();
                    ConfigurationDocument configuration = backend.ReadConfigurationDocument();
                    string identity = configuration.PhoneIdentity;
                    if (identity.Length > 100) identity = identity.Substring(0, 100) + "…";
                    _bindingLabel.Text = "当前绑定：" + (String.IsNullOrWhiteSpace(identity) ? "未绑定" : identity);
                    _wifiIndex.Value = Math.Min(_wifiIndex.Maximum, configuration.WifiInterfaceIndex);
                    _ethernetIndex.Value = Math.Min(_ethernetIndex.Maximum, configuration.EthernetInterfaceIndex);
                    _connectAddress.Text = configuration.ConnectEndpoint;
                }
                catch (Exception exception) { ShowStatus(exception.Message, true); }
            };
            DpiChanged += delegate { UiDpi.ClampToWorkingArea(this, new Size(700, 540)); };
            ResumeLayout(true);
            UiDpi.FitToWorkingArea(this, new Size(920, 855), new Size(700, 540));
        }

        async void PairClicked(object sender, EventArgs args)
        {
            if (busy) return;
            string address = _pairAddress.Text;
            string code = _pairCode.Text;
            int wifi = (int)_wifiIndex.Value;
            _pairCode.Clear();
            await RunOperation(delegate
            {
                try { backend.Pair(address, code, wifi, Progress); }
                finally { code = null; }
            }, false);
        }

        async void SaveClicked(object sender, EventArgs args)
        {
            if (busy) return;
            string address = _connectAddress.Text;
            int wifi = (int)_wifiIndex.Value, ethernet = (int)_ethernetIndex.Value;
            _pairCode.Clear();
            await RunOperation(delegate { backend.VerifyAndSave(address, wifi, ethernet, Progress); }, true);
        }

        async Task RunOperation(Action operation, bool save)
        {
            busy = true; SetEnabled(false); _progress.Visible = true;
            ShowStatus(save ? "正在检测指定手机…" : "正在配对，请保持手机配对码页面打开…", false);
            try
            {
                await Task.Run(operation);
                if (save) ConfigurationSaved = true;
            }
            catch (Exception exception) { ShowStatus(exception.Message, true); }
            finally { busy = false; SetEnabled(true); _progress.Visible = false; _pairCode.Clear(); }
            if (ConfigurationSaved) { DialogResult = DialogResult.OK; Close(); }
        }

        void Progress(string message)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (!IsDisposed) ShowStatus(message, false); }); }
            catch (InvalidOperationException) { }
        }

        void SetEnabled(bool enabled)
        {
            _pairAddress.Enabled = _pairCode.Enabled = _connectAddress.Enabled = enabled;
            _pairButton.Enabled = _saveButton.Enabled = _cancelButton.Enabled = enabled;
            _wifiIndex.Enabled = _ethernetIndex.Enabled = enabled;
        }

        void ShowStatus(string text, bool error)
        {
            _status.Text = text;
            _status.ForeColor = error ? Color.FromArgb(255, 193, 99) : AccentColor;
        }

        void UpdateTarget(TextBox input, Label output)
        {
            if (String.IsNullOrWhiteSpace(input.Text)) { output.Text = "实际地址：输入端口后显示手机热点目标。"; return; }
            try { output.Text = "实际通过手机热点访问：" + backend.ResolveTarget(input.Text, (int)_wifiIndex.Value).Endpoint; }
            catch (Exception exception) { output.Text = exception.Message; }
        }

        static TableLayoutPanel Stack()
        {
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, Margin = Padding.Empty, Size = new Size(1, 1) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            return panel;
        }

        static TableLayoutPanel Rows(int columns, int rows)
        {
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = columns, RowCount = rows, Margin = Padding.Empty, Size = new Size(1, 1) };
            for (int row = 0; row < rows; row++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return panel;
        }

        static void AddRow(TableLayoutPanel parent, Control child, int bottomMargin)
        {
            int row = parent.RowCount++;
            parent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            child.Margin = new Padding(0, 0, 0, bottomMargin);
            parent.Controls.Add(child, 0, row);
        }

        static TableLayoutPanel Section(TableLayoutPanel parent)
        {
            TableLayoutPanel section = Stack();
            section.BackColor = SurfaceColor;
            section.Padding = new Padding(14, 12, 14, 12);
            AddRow(parent, section, 10);
            return section;
        }

        static Label Label(string text, float size, Color color, bool bold)
        {
            return new Label { Text = text, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color,
                Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 3, 0, 3), UseMnemonic = false };
        }

        static Label RowLabel(string text)
        {
            Label label = Label(text, 8.5F, MutedColor, false); label.Dock = DockStyle.Fill; label.TextAlign = ContentAlignment.MiddleLeft; return label;
        }

        static TextBox Input()
        {
            return new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.FromArgb(27, 41, 58), ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 11F), MaxLength = 64, Margin = new Padding(3, 4, 8, 3) };
        }

        static NumericUpDown IndexInput()
        {
            return new NumericUpDown { Minimum = 0, Maximum = Int32.MaxValue, Width = 95, BackColor = Color.FromArgb(27, 41, 58), ForeColor = TextColor,
                Font = new Font("Consolas", 10F), Margin = new Padding(3, 3, 8, 0) };
        }

        static Button Button(string text, bool primary)
        {
            Button button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? AccentColor : Color.FromArgb(27, 41, 58),
                ForeColor = primary ? Color.FromArgb(6, 39, 38) : TextColor, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold), Cursor = Cursors.Hand,
                AutoSize = true, MinimumSize = new Size(0, 36), Margin = new Padding(3, 3, 3, 3), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = primary ? 0 : 1; button.FlatAppearance.BorderColor = Color.FromArgb(40, 57, 75); return button;
        }
    }
}
