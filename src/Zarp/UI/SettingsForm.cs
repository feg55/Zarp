using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Zarp.Core;

namespace Zarp.UI
{
    sealed class SettingsForm : Form
    {
        readonly Engine _engine;
        readonly ListView _list = new ListView();
        readonly Label _status = new Label();
        readonly Button _use, _test, _search, _cancel;
        readonly NumericUpDown _timeout = new NumericUpDown();
        readonly NumericUpDown _stopAfter = new NumericUpDown();
        readonly CheckBox _autoConnect = Check("Подключаться при запуске программы");
        readonly CheckBox _tray = Check("Сворачивать в трей при закрытии окна");
        readonly CheckBox _disconnectOnExit = Check("Отключать WARP при выходе из программы");
        readonly CheckBox _autostart = Check("Запускать вместе с Windows (в трее, с подключением)");
        readonly CheckBox _restrict = Check("Перехватывать только адреса WARP (рекомендуется)");
        readonly CheckBox _isolate = Check("Изолировать тесты: новый эндпоинт WARP на каждый тест");
        readonly CheckBox _autoUpdate = Check("Обновлять zapret2 автоматически (в фоне)");
        readonly Label _zapretVer = new Label();
        bool _loading;

        public SettingsForm(Engine engine)
        {
            _engine = engine;
            Text = "Zarp — настройки";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Font(9f);
            ClientSize = new Size(780, 620);
            MinimumSize = new Size(700, 600);
            ShowInTaskbar = false;
            Theme.DarkTitleBar(this);
            Icon = engine.State == EngineState.Connected ? Theme.AppIcon(32, Theme.Accent) : Theme.AppIcon(32, Theme.Off);

            var header = new Label
            {
                Text = "Стратегия", Font = Theme.Font(12f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 12),
            };
            var explain = new Label
            {
                Text = "Выберите стратегию и нажмите «Использовать». Галочка ✔ — текущая. «работает ✔✔» — прошла две независимые проверки.",
                ForeColor = Theme.TextDim, AutoSize = false, Location = new Point(18, 38), Size = new Size(750, 18),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = true;
            _list.HideSelection = false;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.BackColor = Theme.Panel;
            _list.ForeColor = Theme.Text;
            _list.BorderStyle = BorderStyle.None;
            _list.Columns.Add("", 26);
            _list.Columns.Add("Стратегия", 240);
            _list.Columns.Add("Протокол", 110);
            _list.Columns.Add("Результат", 200);
            _list.Columns.Add("Подкл., мс", 76, HorizontalAlignment.Right);
            _list.Columns.Add("Пинг, мс", 70, HorizontalAlignment.Right);
            _list.SetBounds(16, 60, 748, 250);
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            // шапку рисуем сами: системная всегда светлая
            _list.OwnerDraw = true;
            _list.DrawColumnHeader += DrawHeader;
            _list.DrawItem += (s, e) => e.DrawDefault = true;
            _list.DrawSubItem += (s, e) => e.DrawDefault = true;
            _list.Resize += (s, e) => FitResultColumn();
            Theme.DarkScrollBars(_list);
            _list.DoubleClick += (s, e) => UseSelected();
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();

            _use = Theme.FlatButton("Использовать", 120);
            _use.BackColor = Theme.Accent;
            _use.FlatAppearance.MouseOverBackColor = ControlPaint.Light(Theme.Accent, 0.1f);
            _use.Click += (s, e) => UseSelected();
            _test = Theme.FlatButton("Проверить выбранные", 160);
            _test.Click += async (s, e) =>
            {
                var sel = Selected();
                if (sel.Length > 0) await _engine.SearchAsync(sel);
            };
            _search = Theme.FlatButton("Найти лучшую заново", 160);
            _search.Click += async (s, e) => await _engine.SearchAsync();
            _cancel = Theme.FlatButton("Отмена", 90);
            _cancel.Click += (s, e) => _engine.Cancel();
            var custom = Theme.FlatButton("Свои стратегии...", 130);
            custom.Click += (s, e) => OpenCustomFile();

            var actions = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                Location = new Point(14, 318), Size = new Size(752, 38),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            };
            actions.Controls.AddRange(new Control[] { _use, _test, _search, _cancel, custom });

            _status.ForeColor = Theme.TextDim;
            _status.AutoEllipsis = true;
            _status.SetBounds(18, 360, 748, 20);
            _status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            // ---- параметры
            var opts = new Label
            {
                Text = "Параметры", Font = Theme.Font(12f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 388),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            };
            var left = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Location = new Point(16, 416), Size = new Size(390, 184),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            };
            left.Controls.AddRange(new Control[] { _autoConnect, _autostart, _tray, _disconnectOnExit, _restrict, _isolate, _autoUpdate });

            var right = new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 2, Location = new Point(410, 416), Size = new Size(300, 70),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            };
            SetupNum(_timeout, 5, 60);
            SetupNum(_stopAfter, 0, 100);
            right.Controls.Add(Lbl("Ожидание подключения, с"), 0, 0);
            right.Controls.Add(_timeout, 1, 0);
            right.Controls.Add(Lbl("Остановить поиск после N\nрабочих (0 — проверить все)"), 0, 1);
            right.Controls.Add(_stopAfter, 1, 1);

            var folder = Theme.FlatButton("Папка данных", 140);
            folder.Location = new Point(410, 500);
            folder.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            folder.Click += (s, e) => Process.Start("explorer.exe", "\"" + _engine.DataDir + "\"");
            var defender = Theme.FlatButton("Исключение Защитника", 160);
            defender.Location = new Point(556, 500);
            defender.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            defender.Click += async (s, e) =>
            {
                if (MessageBox.Show(this, "Добавить папку zapret2 в исключения Защитника Windows?\n\n" + _engine.Zapret.Dir,
                        "Zarp", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await _engine.Zapret.AddDefenderExclusionAsync();
            };
            var close = Theme.FlatButton("Закрыть", 100);
            close.Location = new Point(664, 576);
            close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            close.Click += (s, e) => Close();

            var update = Theme.FlatButton("Проверить обновления", 160);
            update.Location = new Point(410, 540);
            update.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            update.Click += async (s, e) =>
            {
                update.Enabled = false;
                _zapretVer.Text = "Проверяю...";
                await _engine.CheckZapretUpdateAsync(true);
                ShowZapretVersion();
                update.Enabled = true;
            };
            _zapretVer.ForeColor = Theme.TextDim;
            _zapretVer.AutoSize = true;
            _zapretVer.Location = new Point(413, 580);
            _zapretVer.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            ShowZapretVersion();

            Controls.AddRange(new Control[] { header, explain, _list, actions, _status, opts, left, right, folder, defender, update, _zapretVer, close });
            LoadOptions();
            foreach (var c in new[] { _autoConnect, _tray, _disconnectOnExit, _restrict, _isolate, _autoUpdate }) c.CheckedChanged += (s, e) => SaveOptions();
            _timeout.ValueChanged += (s, e) => SaveOptions();
            _stopAfter.ValueChanged += (s, e) => SaveOptions();
            _autostart.CheckedChanged += async (s, e) =>
            {
                if (_loading) return;
                bool ok = await Autostart.SetAsync(_autostart.Checked, Application.ExecutablePath);
                if (!ok) { _loading = true; _autostart.Checked = !_autostart.Checked; _loading = false; }
            };

            _engine.Changed += OnEngineChanged;
            FillList();
            FitResultColumn();
            UpdateButtons();
        }

        void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var bg = new SolidBrush(Theme.Back))
                e.Graphics.FillRectangle(bg, e.Bounds);
            using (var line = new Pen(Theme.Border))
                e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            var r = Rectangle.Inflate(e.Bounds, -6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, _list.Font, r, Theme.TextDim, flags);
        }

        /// <summary>Колонка «Результат» забирает свободную ширину, чтобы справа от шапки не оставалось светлой полосы.</summary>
        void FitResultColumn()
        {
            if (_list.Columns.Count < 6) return;
            int others = 0;
            for (int i = 0; i < _list.Columns.Count; i++)
                if (i != 3) others += _list.Columns[i].Width;
            int w = _list.ClientSize.Width - others;
            if (w > 80) _list.Columns[3].Width = w;
        }

        void ShowZapretVersion()
        {
            string v = _engine.Zapret.Version ?? "не установлен";
            _zapretVer.Text = "zapret2 " + v + (_engine.Zapret.UpdatePending ? " (обновление скачано, применится при подключении)" : "");
        }

        static CheckBox Check(string text) => new CheckBox
        {
            Text = text, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(3, 3, 3, 4), FlatStyle = FlatStyle.Flat,
        };

        static Label Lbl(string text) => new Label { Text = text, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(3, 6, 3, 3) };

        static void SetupNum(NumericUpDown n, int min, int max)
        {
            n.Minimum = min; n.Maximum = max; n.Width = 60;
            n.BackColor = Theme.Panel; n.ForeColor = Theme.Text; n.BorderStyle = BorderStyle.FixedSingle;
        }

        async void LoadOptions()
        {
            _loading = true;
            var c = _engine.Config;
            _autoConnect.Checked = c.AutoConnectOnStart;
            _tray.Checked = c.MinimizeToTray;
            _disconnectOnExit.Checked = c.DisconnectOnExit;
            _restrict.Checked = c.RestrictToWarpIps;
            _isolate.Checked = c.IsolateTests;
            _autoUpdate.Checked = c.AutoUpdateZapret;
            _timeout.Value = Math.Max(_timeout.Minimum, Math.Min(_timeout.Maximum, c.TestTimeoutSec));
            _stopAfter.Value = Math.Max(_stopAfter.Minimum, Math.Min(_stopAfter.Maximum, c.StopAfterWorking));
            _autostart.Checked = await Autostart.IsEnabledAsync();
            _loading = false;
        }

        void SaveOptions()
        {
            if (_loading) return;
            var c = _engine.Config;
            c.AutoConnectOnStart = _autoConnect.Checked;
            c.MinimizeToTray = _tray.Checked;
            c.DisconnectOnExit = _disconnectOnExit.Checked;
            c.RestrictToWarpIps = _restrict.Checked;
            c.IsolateTests = _isolate.Checked;
            c.AutoUpdateZapret = _autoUpdate.Checked;
            c.TestTimeoutSec = (int)_timeout.Value;
            c.StopAfterWorking = (int)_stopAfter.Value;
            c.Save();
        }

        void FillList()
        {
            var selectedIds = Selected().Select(s => s.Id).ToList();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var s in _engine.Strategies)
            {
                _engine.Config.Results.TryGetValue(s.Id, out var r);
                bool current = s.Id == _engine.Config.SelectedStrategyId;
                var item = new ListViewItem(new[]
                {
                    current ? "✔" : "",
                    s.Name,
                    Strategy.TransportTitle(s.Transport),
                    r == null ? "не проверялась" : r.Ok ? (r.Confirmed ? "работает ✔✔" : "работает (1 проверка)") : r.Error,
                    r != null && r.Ok ? r.ConnectMs.ToString() : "",
                    r != null && r.Ok ? r.PingMs.ToString() : "",
                })
                {
                    Tag = s,
                    UseItemStyleForSubItems = false,
                    ToolTipText = s.UsesZapret ? s.Args : "WARP без zapret",
                };
                if (current) item.SubItems[1].Font = Theme.Font(9f, FontStyle.Bold);
                item.SubItems[3].ForeColor = r == null ? Theme.TextDim : r.Ok ? (r.Confirmed ? Theme.Ok : Theme.Busy) : Theme.Bad;
                for (int i = 0; i < item.SubItems.Count; i++) item.SubItems[i].BackColor = Theme.Panel;
                item.Selected = selectedIds.Contains(s.Id);
                _list.Items.Add(item);
            }
            _list.ShowItemToolTips = true;
            _list.EndUpdate();
        }

        Strategy[] Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (Strategy)i.Tag).ToArray();

        async void UseSelected()
        {
            var sel = Selected();
            if (sel.Length != 1 || _engine.IsBusy) return;
            await _engine.UseStrategyAsync(sel[0]);
        }

        void OpenCustomFile()
        {
            string path = Path.Combine(_engine.DataDir, StrategyCatalog.CustomFileName);
            if (!File.Exists(path)) _engine.ReloadStrategies(); // создаст шаблон
            try
            {
                using (var p = Process.Start("notepad.exe", "\"" + path + "\""))
                    p?.WaitForExit();
            }
            catch { }
            _engine.ReloadStrategies();
            FillList();
        }

        void OnEngineChanged()
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                if (!_engine.IsBusy || _engine.ProgressDone > 0) FillList();
                UpdateButtons();
            }));
        }

        void UpdateButtons()
        {
            bool busy = _engine.IsBusy;
            int n = _list.SelectedItems.Count;
            _use.Enabled = !busy && n == 1;
            _test.Enabled = !busy && n > 0;
            _search.Enabled = !busy;
            _cancel.Enabled = busy;
            string prog = _engine.ProgressTotal > 0 ? $" [{_engine.ProgressDone}/{_engine.ProgressTotal}]" : "";
            _status.Text = busy ? "⏳ " + _engine.Detail + prog : _engine.Detail;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _engine.Changed -= OnEngineChanged;
            base.OnFormClosed(e);
        }
    }
}
