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
        readonly DarkButton _use, _test, _quick, _full, _cancel;
        readonly ToolTip _tips = new ToolTip();
        readonly NumberBox _timeout = new NumberBox { Minimum = 5, Maximum = 60 };
        // проверить все стратегии теперь можно полным поиском, поэтому здесь минимум - одна рабочая
        readonly NumberBox _stopAfter = new NumberBox { Minimum = 1, Maximum = 100 };
        readonly ToggleSwitch _autoConnect = new ToggleSwitch(L.T("opt.autoConnect"));
        readonly ToggleSwitch _autostart = new ToggleSwitch(L.T("opt.autostart"));
        readonly DarkSelect _closeAction = new DarkSelect(L.T("opt.closeAsk"), L.T("opt.closeTray"), L.T("opt.closeExit"));
        readonly ToggleSwitch _disconnectOnExit = new ToggleSwitch(L.T("opt.disconnectOnExit"));
        readonly ToggleSwitch _restrict = new ToggleSwitch(L.T("opt.restrict"));
        readonly ToggleSwitch _isolate = new ToggleSwitch(L.T("opt.isolate"));
        readonly ToggleSwitch _autoUpdate = new ToggleSwitch(L.T("opt.autoUpdate"));
        readonly Label _zapretVer = new Label();
        bool _loading;

        public SettingsForm(Engine engine)
        {
            _engine = engine;
            Text = L.T("settings.caption");
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

            const int Left0 = 20; // левый отступ
            int W = ClientSize.Width - Left0 * 2;

            // ---------------- стратегии
            var header = Heading(L.T("settings.strategy"), new Point(Left0 - 2, 14));
            var explain = new Label
            {
                Text = L.T("settings.explain"),
                ForeColor = Theme.TextDim, AutoSize = false, Location = new Point(Left0, 42), Size = new Size(W, 20),
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
            _list.Font = Font; // явно: ниже по нему меряются заголовки, а унаследует он его только после добавления на форму
            _list.Columns.Add("", 26);
            _list.Columns.Add(L.T("col.strategy"), 250);
            _list.Columns.Add(L.T("col.protocol"), 110);
            _list.Columns.Add(L.T("col.result"), 200);
            _list.Columns.Add(L.T("col.connect"), 80, HorizontalAlignment.Right);
            _list.Columns.Add(L.T("col.ping"), 72, HorizontalAlignment.Right);
            // заголовки на некоторых языках длиннее: колонка не уже своего заголовка
            foreach (ColumnHeader col in _list.Columns)
                if (col.Index != 3 && col.Text.Length > 0)
                    col.Width = Math.Max(col.Width, TextRenderer.MeasureText(col.Text, _list.Font).Width + 16);
            _list.SetBounds(Left0, 68, W, 250);
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

            _use = Theme.FlatButton(L.T("btn.use"), 110, primary: true);
            _use.Click += (s, e) => UseSelected();
            _test = Theme.FlatButton(L.T("btn.testSelected"), 120);
            _test.Click += async (s, e) =>
            {
                var sel = Selected();
                if (sel.Length > 0) await _engine.TestStrategiesAsync(sel);
            };
            // быстрый поиск останавливается после N рабочих, полный проверяет все стратегии
            _quick = Theme.FlatButton(L.T("btn.quickScan"), 110);
            _quick.Click += async (s, e) => await _engine.SearchAsync(full: false);
            _full = Theme.FlatButton(L.T("btn.fullScan"), 110);
            _full.Click += async (s, e) => await _engine.SearchAsync(full: true);
            _tips.SetToolTip(_full, L.T("tip.fullScan", _engine.Strategies.Count));
            // «Отмена» показывается на месте кнопок поиска, пока идёт поиск: так все кнопки влезают в ряд на любом языке
            _cancel = Theme.FlatButton(L.T("btn.cancel"), 110);
            _cancel.Click += (s, e) => _engine.Cancel();
            var custom = Theme.FlatButton(L.T("btn.custom"), 120);
            custom.Click += (s, e) => OpenCustomFile();

            var actions = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                Location = new Point(Left0, 330), Size = new Size(W, 36),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            };
            actions.Controls.AddRange(new Control[] { _use, _test, _quick, _full, _cancel, custom });

            _status.ForeColor = Theme.TextDim;
            _status.AutoEllipsis = true;
            _status.SetBounds(Left0, 374, W, 20);
            _status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            // ---------------- параметры
            var opts = Heading(L.T("settings.options"), new Point(Left0 - 2, 408));
            opts.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

            // Правая колонка обычно 314 px, но если подписи кнопок длиннее (как «Исключение Защитника»),
            // она расширяется влево, чтобы оставаться выровненной по правому краю окна.
            var folder = Theme.FlatButton(L.T("btn.dataFolder"), 140);
            var defender = Theme.FlatButton(L.T("btn.defender"), 140);
            int rightWidth = Math.Max(314, folder.Width + 8 + defender.Width);
            int extra = rightWidth - (folder.Width + 8 + defender.Width);
            folder.Width += extra / 2;
            defender.Width += extra - extra / 2;
            int R = ClientSize.Width - Left0 - rightWidth;

            var toggles = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Location = new Point(Left0, 442), Size = new Size(R - Left0 - 12, 250),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            };
            var closeOptions = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                Size = new Size(toggles.Width, 32), Margin = new Padding(0, 0, 0, 6),
            };
            closeOptions.Controls.Add(new Label
            {
                Text = L.T("opt.onClose"), AutoSize = true, Margin = new Padding(0, 8, 12, 0),
            });
            _closeAction.AccessibleName = L.T("opt.closeAccessible");
            closeOptions.Controls.Add(_closeAction);
            toggles.Controls.AddRange(new Control[] { _autoConnect, _autostart, closeOptions, _disconnectOnExit, _restrict, _isolate, _autoUpdate });

            // правая колонка: подписи переносятся, поля ввода выровнены по правому краю
            int numberX = ClientSize.Width - Left0 - _timeout.Width;
            int labelWidth = numberX - R - 12;
            var timeoutLbl = Theme.WrappedLabel(L.T("opt.timeout"), Theme.Font(9.5f), Theme.Text, new Point(R, 446), labelWidth);
            _timeout.Location = new Point(numberX, 440);
            var stopLbl = Theme.WrappedLabel(L.T("opt.stopAfter"), Theme.Font(9.5f), Theme.Text, new Point(R, 486), labelWidth);
            _stopAfter.Location = new Point(numberX, 486);

            folder.Location = new Point(R, 542);
            folder.Click += (s, e) => Process.Start("explorer.exe", "\"" + _engine.DataDir + "\"");
            defender.Location = new Point(folder.Right + 8, 542);
            defender.Click += async (s, e) =>
            {
                if (MessageBox.Show(this, L.T("dlg.defender", _engine.Zapret.Dir),
                        "Zarp", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await _engine.Zapret.AddDefenderExclusionAsync();
            };
            var update = Theme.FlatButton(L.T("btn.checkUpdates"), rightWidth);
            update.Location = new Point(R, 584);
            update.Click += async (s, e) =>
            {
                update.Enabled = false;
                _zapretVer.Text = L.T("settings.checking");
                await _engine.CheckZapretUpdateAsync(true);
                ShowZapretVersion();
                update.Enabled = true;
            };
            _zapretVer.ForeColor = Theme.TextDim;
            _zapretVer.AutoSize = true;
            _zapretVer.Location = new Point(R + 2, 626);
            ShowZapretVersion();

            var close = Theme.FlatButton(L.T("btn.close"), 110);
            close.Location = new Point(ClientSize.Width - Left0 - close.Width, ClientSize.Height - 34 - 16);
            close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            close.Click += (s, e) => Close();
            var licenses = Theme.FlatButton(L.T("btn.licenses"), 110);
            licenses.Location = new Point(close.Left - 8 - licenses.Width, close.Top);
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
            _stopAfter.ValueChanged += (s, e) => { SaveOptions(); ShowQuickTip(); };
            _autostart.CheckedChanged += async (s, e) =>
            {
                if (_loading) return;
                bool ok = await Autostart.SetAsync(_autostart.Checked, Application.ExecutablePath);
                if (!ok) { _loading = true; _autostart.Checked = !_autostart.Checked; _loading = false; }
            };

            _engine.Changed += OnEngineChanged;
            FillList();
            FitResultColumn();
            ShowQuickTip();
            UpdateButtons();
        }

        void ShowQuickTip() => _tips.SetToolTip(_quick, L.T("tip.quickScan", _engine.QuickStopAfter));

        static Label Heading(string text, Point at) => new Label
        {
            Text = text, Font = Theme.Font(12f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = true, Location = at,
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
            string v = _engine.Zapret.Version ?? L.T("settings.zapretMissing");
            _zapretVer.Text = "zapret2 " + v + (_engine.Zapret.UpdatePending ? L.T("settings.updatePending") : "");
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
                    r == null ? L.T("result.notTested") : r.Ok ? L.T(r.Confirmed ? "result.works2" : "result.works1") : r.DisplayError,
                    r != null && r.Ok ? r.ConnectMs.ToString() : "",
                    r != null && r.Ok ? r.PingMs.ToString() : "",
                })
                {
                    Tag = s,
                    UseItemStyleForSubItems = false,
                    ToolTipText = s.UsesZapret ? s.Args : L.T("settings.directTip"),
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
            _quick.Visible = _full.Visible = !busy;
            _cancel.Visible = busy;
            string prog = _engine.ProgressTotal > 0 ? $" [{_engine.ProgressDone}/{_engine.ProgressTotal}]" : "";
            _status.Text = busy ? "⏳ " + _engine.Detail + prog : _engine.Detail;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _engine.Changed -= OnEngineChanged;
            _tips.Dispose();
            base.OnFormClosed(e);
        }
    }
}
