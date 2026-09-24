using System;
using System.Drawing;
using System.Windows.Forms;
using Zarp.Core;

namespace Zarp.UI
{
    sealed class MainForm : Form
    {
        readonly Engine _engine;
        readonly bool _autostart, _connectNow;

        readonly PowerButton _power = new PowerButton();
        readonly Label _status = new Label();
        readonly Label _detail = new Label();
        readonly Label _hint = new Label();
        readonly Panel _progress = new Panel();
        readonly LinkLabel _logToggle = new LinkLabel();
        readonly TextBox _log = new TextBox();
        readonly NotifyIcon _tray = new NotifyIcon();
        readonly ToolStripMenuItem _trayToggle = new ToolStripMenuItem();

        Icon _iconOn, _iconOff, _iconBusy;
        bool _exiting, _trayHintShown;
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

            _engine.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)UpdateUi); };
            _engine.AskAntivirusExclusion = AskExclusion;
            _engine.AskContinueWithVpn = AskVpn;
            Log.Line += line => { if (IsHandleCreated) BeginInvoke((Action)(() => AppendLog(line))); };
        }

        void BuildUi()
        {
            var title = new Label
            {
                Text = "Zarp", Font = Theme.Font(18f, FontStyle.Bold), ForeColor = Theme.Text,
                AutoSize = true, Location = new Point(22, 16),
            };
            var sub = new Label
            {
                Text = "Cloudflare WARP поверх zapret2", Font = Theme.Font(9f), ForeColor = Theme.TextDim,
                AutoSize = true, Location = new Point(25, 52),
            };
            var settings = Theme.FlatButton("⚙", 40);
            settings.Font = Theme.Font(14f);
            settings.Height = 40;
            settings.Location = new Point(ClientSize.Width - 62, 20);
            settings.Click += (s, e) => OpenSettings();
            new ToolTip().SetToolTip(settings, "Настройки и выбор стратегии");

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

            _hint.Font = Theme.Font(8.5f);
            _hint.ForeColor = Theme.TextDim;
            _hint.TextAlign = ContentAlignment.MiddleCenter;
            _hint.SetBounds(20, 408, ClientSize.Width - 40, 36);

            _logToggle.Text = "Журнал ▾";
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

            Controls.AddRange(new Control[] { title, sub, settings, _power, _status, _detail, _progress, _hint, _logToggle, _log });
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, (s, e) => ShowFromTray());
            _trayToggle.Click += async (s, e) => await OnPowerClick();
            menu.Items.Add(_trayToggle);
            menu.Items.Add("Настройки", null, (s, e) => { ShowFromTray(); OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, async (s, e) => await ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.Text = "Zarp";
            _tray.Icon = _iconOff;
            _tray.Visible = true;
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
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
                    _status.Text = "Подключено"; _status.ForeColor = Theme.Accent; break;
                case EngineState.Searching:
                    _status.Text = "Поиск стратегии"; _status.ForeColor = Theme.Busy; break;
                case EngineState.Connecting:
                    _status.Text = "Подключение..."; _status.ForeColor = Theme.Busy; break;
                case EngineState.Preparing:
                    _status.Text = "Подготовка..."; _status.ForeColor = Theme.Busy; break;
                case EngineState.Disconnecting:
                    _status.Text = "Отключение..."; _status.ForeColor = Theme.Busy; break;
                default:
                    _status.Text = "Отключено"; _status.ForeColor = Theme.Text; break;
            }
            _detail.Text = _engine.Detail;

            if (busy)
                _hint.Text = "Нажмите на кнопку, чтобы отменить";
            else if (st == EngineState.Connected)
                _hint.Text = "Нажмите, чтобы отключиться";
            else if (_engine.Selected == null)
                _hint.Text = "Нажмите, и Zarp сам найдёт самую быструю стратегию zapret2 и подключит WARP";
            else
                _hint.Text = "Нажмите, чтобы подключиться. Стратегию можно сменить в настройках ⚙";

            _progress.Visible = _engine.ProgressTotal > 0;
            _progress.Invalidate();

            _trayToggle.Text = busy ? "Отменить" : st == EngineState.Connected ? "Отключить" : "Подключить";
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
            _logToggle.Text = show ? "Журнал ▴" : "Журнал ▾";
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
            Func<bool> ask = () => MessageBox.Show(this,
                "Антивирус (обычно Защитник Windows) заблокировал файлы zapret2.\n\n" +
                "Это известное ложное срабатывание на драйвер WinDivert, которым zapret перехватывает трафик.\n\n" +
                "Добавить папку в исключения Защитника Windows и повторить загрузку?\n\n" + dir,
                "Zarp", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            return InvokeRequired ? (bool)Invoke(ask) : ask();
        }

        bool AskVpn(System.Collections.Generic.List<string> adapters)
        {
            if (_autostart && !Visible) return true; // при автозапуске не мешаем диалогами, предупреждение есть в журнале
            Func<bool> ask = () => MessageBox.Show(this,
                "Включён другой VPN:\n\n" + string.Join("\n", adapters) + "\n\n" +
                "Трафик WARP пойдёт через него, и zapret на него не повлияет: WARP может не подключиться, " +
                "а подобранная стратегия будет неверной.\n\nЛучше выключить этот VPN. Всё равно продолжить?",
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
            if (!_exiting && e.CloseReason == CloseReason.UserClosing && _engine.Config.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloonTip(3000, "Zarp работает в фоне",
                        "Иконка в трее. Чтобы выйти совсем: правый клик → Выход.", ToolTipIcon.Info);
                }
                return;
            }
            if (!_exiting)
            {
                e.Cancel = true;
                await ExitApp();
                return;
            }
            base.OnFormClosing(e);
        }

        System.Threading.Tasks.Task ExitApp() => ExitAsync(handover: false);

        /// <summary>Другая копия Zarp (например, новая версия) попросила уступить ей место.</summary>
        public System.Threading.Tasks.Task ExitForHandoverAsync() => ExitAsync(handover: true);

        async System.Threading.Tasks.Task ExitAsync(bool handover)
        {
            if (_exiting) return;
            _exiting = true;
            Hide();
            _tray.Visible = false;
            if (handover)
            {
                // winws2 и подключение принадлежат этой копии: освобождаем всё, новая копия подключится сама
                Log.Write("Другая копия Zarp попросила закрыться: освобождаю WARP и winws2.");
                await _engine.StopForHandoverAsync();
            }
            else
            {
                await _engine.ShutdownAsync();
            }
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tray.Dispose();
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
