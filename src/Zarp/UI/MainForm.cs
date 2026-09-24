using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Zarp.Core;

namespace Zarp.UI
{
    sealed class MainForm : Form
    {
        readonly Engine _engine;
        readonly bool _autostart, _connectNow;

        readonly PowerButton _power = new PowerButton();
        readonly Label _sub = new Label();
        readonly Label _status = new Label();
        readonly Label _detail = new Label();
        readonly Label _hint = new Label();
        readonly Panel _progress = new Panel();
        readonly LinkLabel _logToggle = new LinkLabel();
        readonly TextBox _log = new TextBox();
        readonly SettingsButton _settings = new SettingsButton();
        readonly LanguageButton _language = new LanguageButton();
        readonly ToolTip _tips = new ToolTip();
        readonly ContextMenuStrip _languageMenu = new ContextMenuStrip();
        readonly NotifyIcon _tray = new NotifyIcon();
        readonly ToolStripMenuItem _trayOpen = new ToolStripMenuItem();
        readonly ToolStripMenuItem _trayToggle = new ToolStripMenuItem();
        readonly ToolStripMenuItem _traySettings = new ToolStripMenuItem();
        readonly ToolStripMenuItem _trayExit = new ToolStripMenuItem();

        Icon _iconOn, _iconOff, _iconBusy;
        bool _exiting, _exitComplete, _trayHintShown;
        CloseActionForm _closePrompt;
        const int CompactHeight = 500, LogHeight = 190;

        public MainForm(Engine engine, bool autostart, bool connectNow)
        {
            _engine = engine;
            _autostart = autostart;
            _connectNow = connectNow;

            Text = "Zarp";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Font(9f);
            ClientSize = new Size(380, CompactHeight);
            Theme.DarkTitleBar(this);

            _iconOn = Theme.AppIcon(32, Theme.Accent);
            _iconOff = Theme.AppIcon(32, Theme.Off);
            _iconBusy = Theme.AppIcon(32, Theme.Busy);
            Icon = _iconOn;

            BuildUi();
            BuildTray();
            ApplyTexts();

            _engine.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)UpdateUi); };
            _engine.AskAntivirusExclusion = AskExclusion;
            _engine.AskContinueWithVpn = AskVpn;
            Log.Line += line => { if (IsHandleCreated) BeginInvoke((Action)(() => AppendLog(line))); };
            L.Changed += ApplyTexts;
        }

        void BuildUi()
        {
            var title = new Label
            {
                Text = "Zarp", Font = Theme.Font(18f, FontStyle.Bold), ForeColor = Theme.Text,
                AutoSize = true, Location = new Point(22, 16),
            };
            _sub.Font = Theme.Font(9f);
            _sub.ForeColor = Theme.TextDim;
            _sub.AutoSize = true;
            _sub.Location = new Point(25, 52);

            _settings.Location = new Point(ClientSize.Width - 62, 20);
            _settings.Click += (s, e) => OpenSettings();
            _language.Location = new Point(_settings.Left - _language.Width - 6, 20);
            _language.Click += (s, e) => ShowLanguageMenu();

            _languageMenu.Renderer = new DarkMenuRenderer();
            _languageMenu.ShowImageMargin = false;
            _languageMenu.ShowCheckMargin = false;
            _languageMenu.Padding = new Padding(3);

            _power.Size = new Size(200, 200);
            _power.Location = new Point((ClientSize.Width - 200) / 2, 92);
            _power.Click += async (s, e) => await OnPowerClick();

            _status.Font = Theme.Font(16f, FontStyle.Bold);
            _status.TextAlign = ContentAlignment.MiddleCenter;
            _status.SetBounds(10, 306, ClientSize.Width - 20, 34);

            _detail.Font = Theme.Font(9.5f);
            _detail.ForeColor = Theme.TextDim;
            _detail.TextAlign = ContentAlignment.TopCenter;
            _detail.AutoEllipsis = true;
            _detail.SetBounds(20, 342, ClientSize.Width - 40, 40);

            _progress.SetBounds(50, 390, ClientSize.Width - 100, 4);
            _progress.BackColor = Theme.Panel;
            _progress.Paint += PaintProgress;

            // три строки: подсказка на некоторых языках заметно длиннее русской
            _hint.Font = Theme.Font(8.5f);
            _hint.ForeColor = Theme.TextDim;
            _hint.TextAlign = ContentAlignment.MiddleCenter;
            _hint.SetBounds(20, 404, ClientSize.Width - 40, 52);

            _logToggle.LinkColor = Theme.TextDim;
            _logToggle.ActiveLinkColor = Theme.Text;
            _logToggle.LinkBehavior = LinkBehavior.HoverUnderline;
            _logToggle.AutoSize = true;
            _logToggle.Location = new Point(22, CompactHeight - 34);
            _logToggle.Click += (s, e) => ToggleLog();

            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = Theme.Panel;
            _log.ForeColor = Theme.TextDim;
            _log.Font = new Font("Consolas", 8.5f);
            _log.SetBounds(14, CompactHeight - 6, ClientSize.Width - 28, LogHeight - 14);
            _log.Visible = false;
            Theme.DarkScrollBars(_log);

            Controls.AddRange(new Control[] { title, _sub, _language, _settings, _power, _status, _detail, _progress, _hint, _logToggle, _log });
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
            _trayOpen.Click += (s, e) => ShowFromTray();
            _trayToggle.Click += async (s, e) => await OnPowerClick();
            _traySettings.Click += (s, e) => { ShowFromTray(); OpenSettings(); };
            _trayExit.Click += async (s, e) => await ExitApp();
            menu.Items.AddRange(new ToolStripItem[] { _trayOpen, _trayToggle, _traySettings, new ToolStripSeparator(), _trayExit });
            _tray.ContextMenuStrip = menu;
            _tray.Text = "Zarp";
            _tray.Icon = _iconOff;
            _tray.Visible = true;
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        }

        /// <summary>Все тексты окна и меню трея на текущем языке. Вызывается и при смене языка.</summary>
        void ApplyTexts()
        {
            _sub.Text = L.T("main.subtitle");
            _tips.SetToolTip(_settings, L.T("main.settingsTip"));
            _tips.SetToolTip(_language, L.T("main.languageTip"));
            _settings.AccessibleName = L.T("main.settingsTip");
            _language.AccessibleName = L.T("main.languageTip");
            _logToggle.Text = L.T(_log.Visible ? "main.logHide" : "main.logShow");
            _trayOpen.Text = L.T("tray.open");
            _traySettings.Text = L.T("tray.settings");
            _trayExit.Text = L.T("tray.exit");
            UpdateUi();
        }

        void ShowLanguageMenu()
        {
            if (_languageMenu.Visible) { _languageMenu.Close(); return; }
            string setting = _engine.Config.Language;
            bool followSystem = !L.IsSupported(setting);
            string system = L.Languages.First(l => l.Code == L.SystemLanguage).NativeName;

            _languageMenu.Items.Clear();
            _languageMenu.Font = Theme.Font(9.5f);
            AddLanguageItem(L.T("lang.system", system), null, followSystem);
            _languageMenu.Items.Add(new ToolStripSeparator());
            foreach (var language in L.Languages)
                AddLanguageItem(language.NativeName, language.Code, !followSystem && setting == language.Code);

            // строки одной высоты и ширины, как в выпадающем списке настроек
            var items = _languageMenu.Items.OfType<ToolStripMenuItem>().ToList();
            int width = Math.Max(180, items.Max(i => TextRenderer.MeasureText(i.Text, _languageMenu.Font).Width) + 36);
            foreach (var item in items)
            {
                item.AutoSize = false;
                item.Size = new Size(width, Math.Max(30, _languageMenu.Font.Height + 12));
            }
            _languageMenu.Show(_language, new Point(_language.Width - width - _languageMenu.Padding.Horizontal, _language.Height + 4));
        }

        void AddLanguageItem(string text, string code, bool current)
        {
            var item = new ToolStripMenuItem(text) { Checked = current };
            item.Click += (s, e) => SetLanguage(code);
            _languageMenu.Items.Add(item);
        }

        /// <summary>null - как в системе.</summary>
        void SetLanguage(string code)
        {
            if (_engine.Config.Language == code) return;
            _engine.Config.Language = code;
            _engine.Config.Save();
            L.Apply(code); // L.Changed → ApplyTexts
        }

        protected override void SetVisibleCore(bool value)
        {
            // при автозапуске стартуем сразу в трее
            if (_autostart && !IsHandleCreated)
            {
                CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override async void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateUi();
            foreach (var line in StartupLog.Drain()) AppendLog(line);
            await _engine.RefreshStateAsync();
            if (_exiting || IsDisposed) return;
            _engine.StartBackgroundUpdates();
            if (_engine.State != EngineState.Idle) return;
            // --connect: подключиться сразу (найдёт стратегию, если её ещё нет)
            if (_connectNow || ((_autostart || _engine.Config.AutoConnectOnStart) && _engine.Selected != null))
                await _engine.ConnectAsync();
        }

        async System.Threading.Tasks.Task OnPowerClick()
        {
            if (_engine.IsBusy)
            {
                _engine.Cancel();
                return;
            }
            if (_engine.State == EngineState.Connected)
                await _engine.DisconnectAsync();
            else
                await _engine.ConnectAsync();
        }

        void UpdateUi()
        {
            var st = _engine.State;
            bool busy = _engine.IsBusy;
            _power.State = st == EngineState.Connected ? PowerButton.Look.On : busy ? PowerButton.Look.Busy : PowerButton.Look.Off;

            switch (st)
            {
                case EngineState.Connected:
                    _status.Text = L.T("status.connected"); _status.ForeColor = Theme.Accent; break;
                case EngineState.Searching:
                    _status.Text = L.T("status.searching"); _status.ForeColor = Theme.Busy; break;
                case EngineState.Connecting:
                    _status.Text = L.T("status.connecting"); _status.ForeColor = Theme.Busy; break;
                case EngineState.Preparing:
                    _status.Text = L.T("status.preparing"); _status.ForeColor = Theme.Busy; break;
                case EngineState.Disconnecting:
                    _status.Text = L.T("status.disconnecting"); _status.ForeColor = Theme.Busy; break;
                default:
                    _status.Text = L.T("status.disconnected"); _status.ForeColor = Theme.Text; break;
            }
            _detail.Text = _engine.Detail;

            if (busy)
                _hint.Text = L.T("hint.cancel");
            else if (st == EngineState.Connected)
                _hint.Text = L.T("hint.disconnect");
            else if (_engine.Selected == null)
                _hint.Text = L.T("hint.firstRun");
            else
                _hint.Text = L.T("hint.connect");

            _progress.Visible = _engine.ProgressTotal > 0;
            _progress.Invalidate();

            _trayToggle.Text = L.T(busy ? "tray.cancel" : st == EngineState.Connected ? "tray.disconnect" : "tray.connect");
            _tray.Icon = st == EngineState.Connected ? _iconOn : busy ? _iconBusy : _iconOff;
            string trayText = "Zarp: " + _status.Text;
            _tray.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText;
        }

        void PaintProgress(object sender, PaintEventArgs e)
        {
            if (_engine.ProgressTotal <= 0) return;
            float frac = Math.Min(1f, _engine.ProgressDone / (float)_engine.ProgressTotal);
            using (var br = new SolidBrush(Theme.Busy))
                e.Graphics.FillRectangle(br, 0, 0, _progress.Width * frac, _progress.Height);
        }

        void ToggleLog()
        {
            bool show = !_log.Visible;
            _log.Visible = show;
            _logToggle.Text = L.T(show ? "main.logHide" : "main.logShow");
            ClientSize = new Size(ClientSize.Width, CompactHeight + (show ? LogHeight : 0));
            if (show) { _log.SelectionStart = _log.TextLength; _log.ScrollToCaret(); }
        }

        void AppendLog(string line)
        {
            if (_log.TextLength > 60000) _log.Text = _log.Text.Substring(30000);
            _log.AppendText(line + Environment.NewLine);
        }

        bool AskExclusion(string dir)
        {
            Func<bool> ask = () => MessageBox.Show(this, L.T("dlg.antivirus", dir),
                "Zarp", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            return InvokeRequired ? (bool)Invoke(ask) : ask();
        }

        bool AskVpn(System.Collections.Generic.List<string> adapters)
        {
            if (_autostart && !Visible) return true; // при автозапуске не мешаем диалогами, предупреждение есть в журнале
            Func<bool> ask = () => MessageBox.Show(this, L.T("dlg.vpn", string.Join("\n", adapters)),
                "Zarp", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            return InvokeRequired ? (bool)Invoke(ask) : ask();
        }

        void OpenSettings()
        {
            using (var f = new SettingsForm(_engine))
                f.ShowDialog(this);
            UpdateUi();
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            // Не закрывать окно повторным запросом, пока выполняется отключение.
            if (_exiting)
            {
                e.Cancel = !_exitComplete;
                base.OnFormClosing(e);
                return;
            }
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (_closePrompt != null) return;
                bool minimizeToTray = _engine.Config.MinimizeToTray;
                if (_engine.Config.AskBeforeClose)
                {
                    using (var prompt = new CloseActionForm(minimizeToTray, _engine.Config.DisconnectOnExit))
                    {
                        _closePrompt = prompt;
                        DialogResult result;
                        try { result = prompt.ShowDialog(this); }
                        finally { _closePrompt = null; }
                        if (_exiting || result != DialogResult.OK) return;
                        minimizeToTray = prompt.MinimizeToTray;
                        if (prompt.RememberChoice)
                        {
                            _engine.Config.MinimizeToTray = minimizeToTray;
                            _engine.Config.AskBeforeClose = false;
                            _engine.Config.Save();
                        }
                    }
                }
                if (minimizeToTray)
                {
                    Hide();
                    if (!_trayHintShown)
                    {
                        _trayHintShown = true;
                        _tray.ShowBalloonTip(3000, L.T("tray.bgTitle"), L.T("tray.bgText"), ToolTipIcon.Info);
                    }
                    return;
                }
            }
            e.Cancel = true;
            await ExitApp();
        }

        System.Threading.Tasks.Task ExitApp() => ExitAsync(handover: false);

        /// <summary>Другая копия Zarp (например, новая версия) попросила уступить ей место.</summary>
        public System.Threading.Tasks.Task ExitForHandoverAsync() => ExitAsync(handover: true);

        async System.Threading.Tasks.Task ExitAsync(bool handover)
        {
            if (_exiting) return;
            _exiting = true;
            if (_closePrompt != null) _closePrompt.DialogResult = DialogResult.Cancel;
            Hide();
            _tray.Visible = false;
            if (handover)
            {
                // winws2 и подключение принадлежат этой копии: освобождаем всё, новая копия подключится сама
                Log.Write(L.T("log.handover"));
                await _engine.StopForHandoverAsync();
            }
            else
            {
                await _engine.ShutdownAsync();
            }
            _exitComplete = true;
            // Если отключение завершилось синхронно, сначала дать закончиться OnFormClosing.
            BeginInvoke((Action)Close);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                L.Changed -= ApplyTexts;
                _tray.Dispose();
                _tips.Dispose();
                _languageMenu.Dispose();
                _iconOn?.Dispose(); _iconOff?.Dispose(); _iconBusy?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Строки лога, записанные до создания окна.</summary>
    static class StartupLog
    {
        static readonly System.Collections.Generic.List<string> Lines = new System.Collections.Generic.List<string>();
        static bool _closed;
        public static void Add(string line) { lock (Lines) if (!_closed) Lines.Add(line); }
        public static string[] Drain() { lock (Lines) { _closed = true; var a = Lines.ToArray(); Lines.Clear(); return a; } }
    }
}
