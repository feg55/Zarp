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
        readonly DarkButton _use, _test, _search, _cancel;
        readonly NumberBox _timeout = new NumberBox { Minimum = 5, Maximum = 60 };
        readonly NumberBox _stopAfter = new NumberBox { Minimum = 0, Maximum = 100 };
        readonly ToggleSwitch _autoConnect = new ToggleSwitch("Подключаться при запуске программы");
        readonly ToggleSwitch _autostart = new ToggleSwitch("Запускать вместе с Windows");
        readonly ComboBox _closeAction = new ComboBox();
        readonly ToggleSwitch _disconnectOnExit = new ToggleSwitch("Отключать WARP при выходе");
        readonly ToggleSwitch _restrict = new ToggleSwitch("Перехватывать только адреса WARP");
        readonly ToggleSwitch _isolate = new ToggleSwitch("Изолировать тесты (новый эндпоинт на каждый)");
        readonly ToggleSwitch _autoUpdate = new ToggleSwitch("Обновлять zapret2 автоматически");
        readonly Label _zapretVer = new Label();
        bool _loading;

        public SettingsForm(Engine engine)
        {
            _engine = engine;
            Text = "Настройки Zarp";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Font(9f);
            ClientSize = new Size(800, 720);
            MinimumSize = new Size(760, 700);
            ShowInTaskbar = false;
            Theme.DarkTitleBar(this);
            Icon = engine.State == EngineState.Connected ? Theme.AppIcon(32, Theme.Accent) : Theme.AppIcon(32, Theme.Off);

            const int L = 20; // левый отступ
            int W = ClientSize.Width - L * 2;

            // ---------------- стратегии
            var header = Heading("Стратегия", new Point(L - 2, 14));
            var explain = new Label
            {
                Text = "Двойной клик или «Использовать» включает стратегию. ✔ текущая, ✔✔ прошла две независимые проверки.",
                ForeColor = Theme.TextDim, AutoSize = false, Location = new Point(L, 42), Size = new Size(W, 20),
                AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
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
            _list.Columns.Add("Стратегия", 250);
            _list.Columns.Add("Протокол", 110);
            _list.Columns.Add("Результат", 200);
            _list.Columns.Add("Подкл., мс", 80, HorizontalAlignment.Right);
            _list.Columns.Add("Пинг, мс", 72, HorizontalAlignment.Right);
            _list.SetBounds(L, 68, W, 250);
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

            _use = Theme.FlatButton("Использовать", 130, primary: true);
            _use.Click += (s, e) => UseSelected();
            _test = Theme.FlatButton("Проверить выбранные", 170);
            _test.Click += async (s, e) =>
            {
                var sel = Selected();
                if (sel.Length > 0) await _engine.SearchAsync(sel);
            };
            _search = Theme.FlatButton("Найти лучшую заново", 170);
            _search.Click += async (s, e) => await _engine.SearchAsync();
            _cancel = Theme.FlatButton("Отмена", 90);
            _cancel.Click += (s, e) => _engine.Cancel();
            var custom = Theme.FlatButton("Свои стратегии...", 140);
            custom.Click += (s, e) => OpenCustomFile();

            var actions = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                Location = new Point(L, 330), Size = new Size(W, 36),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            };
            actions.Controls.AddRange(new Control[] { _use, _test, _search, _cancel, custom });

            _status.ForeColor = Theme.TextDim;
            _status.AutoEllipsis = true;
            _status.SetBounds(L, 374, W, 20);
            _status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            // ---------------- параметры
            var opts = Heading("Параметры", new Point(L - 2, 408));
            opts.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

            var toggles = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Location = new Point(L, 442), Size = new Size(420, 250),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            };
            var closeOptions = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                Size = new Size(420, 32), Margin = new Padding(0, 0, 0, 6),
            };
            closeOptions.Controls.Add(new Label
            {
                Text = "При закрытии:", AutoSize = true, Margin = new Padding(0, 5, 12, 0),
            });
            _closeAction.DropDownStyle = ComboBoxStyle.DropDownList;
            _closeAction.FlatStyle = FlatStyle.Flat;
            _closeAction.BackColor = Theme.Panel;
            _closeAction.ForeColor = Theme.Text;
            _closeAction.DrawMode = DrawMode.OwnerDrawFixed;
            _closeAction.DrawItem += (s, e) =>
            {
                using (var background = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Theme.PanelHover : Theme.Panel))
                    e.Graphics.FillRectangle(background, e.Bounds);
                if (e.Index >= 0)
                    TextRenderer.DrawText(e.Graphics, _closeAction.Items[e.Index].ToString(), e.Font,
                        e.Bounds, Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                e.DrawFocusRectangle();
            };
            _closeAction.Width = 240;
            _closeAction.AccessibleName = "Действие при закрытии окна";
            _closeAction.Items.AddRange(new object[] { "Спрашивать каждый раз", "Скрывать в трей", "Закрывать приложение" });
            closeOptions.Controls.Add(_closeAction);
            toggles.Controls.AddRange(new Control[] { _autoConnect, _autostart, closeOptions, _disconnectOnExit, _restrict, _isolate, _autoUpdate });

            const int R = 470; // правая колонка
            var timeoutLbl = Lbl("Ожидание подключения, с", new Point(R, 446));
            _timeout.Location = new Point(R + 200, 440);
            var stopLbl = Lbl("Сколько рабочих найти\n(0 = проверить все)", new Point(R, 486));
            _stopAfter.Location = new Point(R + 200, 486);

            var folder = Theme.FlatButton("Папка данных", 150);
            folder.Location = new Point(R, 542);
            folder.Click += (s, e) => Process.Start("explorer.exe", "\"" + _engine.DataDir + "\"");
            var defender = Theme.FlatButton("Исключение Защитника", 160);
            defender.Location = new Point(R + 158, 542);
            defender.Click += async (s, e) =>
            {
                if (MessageBox.Show(this, "Добавить папку zapret2 в исключения Защитника Windows?\n\n" + _engine.Zapret.Dir,
                        "Zarp", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await _engine.Zapret.AddDefenderExclusionAsync();
            };
            var update = Theme.FlatButton("Проверить обновления", 318);
            update.Location = new Point(R, 584);
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
            _zapretVer.Location = new Point(R + 2, 626);
            ShowZapretVersion();

            var close = Theme.FlatButton("Закрыть", 110);
            close.Location = new Point(ClientSize.Width - L - 110, ClientSize.Height - 34 - 16);
            close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            close.Click += (s, e) => Close();
            var licenses = Theme.FlatButton("Лицензии", 110);
            licenses.Location = new Point(close.Left - 118, close.Top);
            licenses.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            licenses.Click += (s, e) =>
            {
                string dir = Licenses.Extract(_engine.DataDir);
                Process.Start("explorer.exe", "/select,\"" + Path.Combine(dir, Licenses.NoticesFile) + "\"");
            };

            foreach (var c in new Control[] { timeoutLbl, _timeout, stopLbl, _stopAfter, folder, defender, update, _zapretVer })
                c.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

            Controls.AddRange(new Control[]
            {
                header, explain, _list, actions, _status, opts, toggles,
                timeoutLbl, _timeout, stopLbl, _stopAfter, folder, defender, update, _zapretVer, licenses, close,
            });

            LoadOptions();
            foreach (var c in new[] { _autoConnect, _disconnectOnExit, _restrict, _isolate, _autoUpdate })
                c.CheckedChanged += (s, e) => SaveOptions();
            _closeAction.SelectedIndexChanged += (s, e) => SaveOptions();
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

        static Label Heading(string text, Point at) => new Label
        {
            Text = text, Font = Theme.Font(12f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = true, Location = at,
        };

        static Label Lbl(string text, Point at) => new Label
        {
            Text = text, AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(9.5f), Location = at,
        };

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
            _zapretVer.Text = "zapret2 " + v + (_engine.Zapret.UpdatePending ? ", обновление применится при подключении" : "");
        }

        async void LoadOptions()
        {
            _loading = true;
            var c = _engine.Config;
            _autoConnect.Checked = c.AutoConnectOnStart;
            _closeAction.SelectedIndex = c.AskBeforeClose ? 0 : c.MinimizeToTray ? 1 : 2;
            _disconnectOnExit.Checked = c.DisconnectOnExit;
            _restrict.Checked = c.RestrictToWarpIps;
            _isolate.Checked = c.IsolateTests;
            _autoUpdate.Checked = c.AutoUpdateZapret;
            _timeout.Value = c.TestTimeoutSec;
            _stopAfter.Value = c.StopAfterWorking;
            _loading = false;
            _autostart.Enabled = false;
            bool autostart = await Autostart.IsEnabledAsync();
            if (IsDisposed) return;
            _loading = true;
            _autostart.Checked = autostart;
            _autostart.Enabled = true;
            _loading = false;
        }

        void SaveOptions()
        {
            if (_loading) return;
            var c = _engine.Config;
            c.AutoConnectOnStart = _autoConnect.Checked;
            c.AskBeforeClose = _closeAction.SelectedIndex == 0;
            if (!c.AskBeforeClose) c.MinimizeToTray = _closeAction.SelectedIndex == 1;
            c.DisconnectOnExit = _disconnectOnExit.Checked;
            c.RestrictToWarpIps = _restrict.Checked;
            c.IsolateTests = _isolate.Checked;
            c.AutoUpdateZapret = _autoUpdate.Checked;
            c.TestTimeoutSec = _timeout.Value;
            c.StopAfterWorking = _stopAfter.Value;
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
