using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Zarp.Core;

namespace Zarp.UI
{
    /// <summary>
    /// Маршрутизация собственного сервера: включение, выбор набора правил (действует один), правка, создание и удаление наборов,
    /// переход к базам GeoIP и GeoSite. Изменения применяются при следующем подключении.
    /// </summary>
    sealed class RoutingForm : DialogBase
    {
        readonly Engine _engine;
        readonly ToggleSwitch _enable = new ToggleSwitch(L.T("routing.enable"));
        readonly DarkListView _list = new DarkListView();
        readonly DarkButton _use, _edit, _add, _remove, _geo, _close;
        readonly string _deleteText = L.T("routing.deletePreset"), _resetText = L.T("routing.resetPreset");

        /// <summary>Название набора: своё имя, а у встроенных - из перевода по Id.</summary>
        internal static string PresetName(RoutingPreset preset)
        {
            if (!string.IsNullOrWhiteSpace(preset.Name)) return preset.Name;
            switch (preset.Id)
            {
                case "lan": return L.T("routing.lanPreset");
                case "ru": return L.T("country.ru");
                case "ir": return L.T("country.ir");
                case "cn": return L.T("country.cn");
                default: return L.T("routing.customPreset");
            }
        }

        RoutingSettings Routing => _engine.Config.Routing;

        public RoutingForm(Engine engine) : base(L.T("routing.title"))
        {
            _engine = engine;
            const int Pad = 20, W = 660;
            var title = Heading(L.T("routing.title"), new Point(Pad - 2, 16));
            _enable.Location = new Point(Pad, title.Bottom + 12);
            int y = _enable.Bottom + 6;
            Label onlyProxy = null;
            if (!engine.ProxyMode)
            {
                onlyProxy = Note(L.T("routing.onlyProxy"), new Point(Pad, y), W);
                y = onlyProxy.Bottom + 8;
            }
            var presets = new Label { Text = L.T("routing.presets"), Font = Theme.Font(10.5f, FontStyle.Bold), AutoSize = true, Location = new Point(Pad, y + 4) };

            _list.Columns.Add("", 28);
            _list.Columns.Add(L.T("routing.presets"), 250);
            _list.Columns.Add(L.T("routing.direct"), 90, HorizontalAlignment.Right);
            _list.Columns.Add(L.T("routing.proxy"), 90, HorizontalAlignment.Right);
            _list.Columns.Add(L.T("routing.block"), 90, HorizontalAlignment.Right);
            foreach (ColumnHeader col in _list.Columns)
                if (col.Index > 1) col.Width = Math.Max(col.Width, TextRenderer.MeasureText(col.Text, _list.Font).Width + 24);
            _list.MultiSelect = false;
            _list.Bounds = new Rectangle(Pad, presets.Bottom + 8, W, 190);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.DoubleClick += (s, e) => UseSelected();
            _list.Resize += (s, e) => _list.FitColumn(1);

            _use = Theme.FlatButton(L.T("btn.use"), 100, primary: true);
            _edit = Theme.FlatButton(L.T("btn.edit"), 110);
            _add = Theme.FlatButton(L.T("routing.addPreset"), 130);
            _remove = Theme.FlatButton(TextRenderer.MeasureText(_deleteText, Font).Width >= TextRenderer.MeasureText(_resetText, Font).Width ? _deleteText : _resetText, 130);
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                Location = new Point(Pad, _list.Bottom + 10), Size = new Size(W, 36),
            };
            buttons.Controls.AddRange(new Control[] { _use, _edit, _add, _remove });
            _use.Click += (s, e) => UseSelected();
            _edit.Click += (s, e) => EditSelected();
            _add.Click += (s, e) => AddPreset();
            _remove.Click += (s, e) => RemoveSelected();

            var priority = Note(L.T("routing.priority"), new Point(Pad, buttons.Bottom + 8), W);
            _geo = Theme.FlatButton(L.T("btn.geo"), 190);
            _geo.Location = new Point(Pad, priority.Bottom + 8);
            _geo.Click += (s, e) => { using (var f = new GeoSourcesForm(_engine)) f.ShowDialog(this); };
            var reconnect = Note(L.T("settings.reconnect"), new Point(Pad, _geo.Bottom + 8), W);
            _close = Theme.FlatButton(L.T("btn.close"), 110);
            _close.Location = new Point(Pad + W - _close.Width, reconnect.Bottom + 12);
            _close.Click += (s, e) => Close();

            ClientSize = new Size(W + Pad * 2, _close.Bottom + 18);
            Controls.AddRange(new Control[] { title, _enable, presets, _list, buttons, priority, _geo, reconnect, _close });
            if (onlyProxy != null) Controls.Add(onlyProxy);

            _enable.Checked = Routing.Enabled;
            _enable.CheckedChanged += (s, e) =>
            {
                Routing.Enabled = _enable.Checked;
                _engine.Config.Save();
            };
            FillList(Routing.SelectedPreset);
            _list.FitColumn(1);
        }

        RoutingPreset Selected => _list.SelectedItems.Count == 1 ? (RoutingPreset)_list.SelectedItems[0].Tag : null;

        static int Count(string text) => RoutingPreset.Lines(text).Count;

        void FillList(string select)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var p in Routing.Presets)
            {
                var item = new ListViewItem(new[]
                {
                    p.Id == Routing.SelectedPreset ? "✔" : "", PresetName(p), Count(p.Direct).ToString(), Count(p.Proxy).ToString(), Count(p.Block).ToString(),
                }) { Tag = p, UseItemStyleForSubItems = false };
                if (p.Id == Routing.SelectedPreset) item.SubItems[1].Font = Theme.Font(9f, FontStyle.Bold);
                foreach (ListViewItem.ListViewSubItem sub in item.SubItems) sub.BackColor = Theme.Panel;
                item.Selected = p.Id == select;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            var p = Selected;
            _use.Enabled = p != null && p.Id != Routing.SelectedPreset;
            _edit.Enabled = p != null;
            bool builtIn = p != null && RoutingSettings.IsBuiltIn(p.Id);
            _remove.Text = builtIn ? _resetText : _deleteText;
            _remove.Enabled = p != null && (!builtIn || !RoutingSettings.Original(p.Id).SameRules(p));
        }

        void Save(string select)
        {
            _engine.Config.Save();
            FillList(select);
        }

        void UseSelected()
        {
            var p = Selected;
            if (p == null) return;
            Routing.SelectedPreset = p.Id;
            Save(p.Id);
        }

        void EditSelected()
        {
            var p = Selected;
            if (p == null) return;
            using (var f = new PresetForm(p, false))
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    int index = Routing.Presets.FindIndex(x => x.Id == p.Id);
                    if (index >= 0) Routing.Presets[index] = f.Result;
                    Save(p.Id);
                }
        }

        void AddPreset()
        {
            var created = new RoutingPreset { Id = "custom-" + Guid.NewGuid().ToString("N"), Name = L.T("routing.customPreset") };
            using (var f = new PresetForm(created, true))
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    Routing.Presets.Add(f.Result);
                    Save(f.Result.Id);
                }
        }

        void RemoveSelected()
        {
            var p = Selected;
            if (p == null) return;
            if (RoutingSettings.IsBuiltIn(p.Id))
            {
                int index = Routing.Presets.FindIndex(x => x.Id == p.Id);
                Routing.Presets[index] = RoutingSettings.Original(p.Id);
                Save(p.Id);
                return;
            }
            if (MessageBox.Show(this, L.T("routing.deleteConfirm", PresetName(p)), "Zarp", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Routing.Presets.RemoveAll(x => x.Id == p.Id);
            if (Routing.SelectedPreset == p.Id)
            {
                // удалён действующий набор: без него правил нет, поэтому маршрутизация выключается
                Routing.SelectedPreset = "lan";
                Routing.Enabled = false;
                _enable.Checked = false;
            }
            Save("lan");
        }
    }

    /// <summary>
    /// Правка набора: три группы правил рядом. Название можно менять только у своих наборов; у встроенных можно вернуть исходные
    /// правила. Синтаксис проверяется при сохранении, наличие баз и категорий - при подключении.
    /// </summary>
    sealed class PresetForm : DialogBase
    {
        readonly RoutingPreset _preset;
        readonly bool _custom;
        readonly DarkTextBox _name = new DarkTextBox(false);
        readonly DarkTextBox _direct = new DarkTextBox(true), _proxy = new DarkTextBox(true), _block = new DarkTextBox(true);
        readonly Label _error = new Label();
        readonly DarkButton _save, _cancel, _reset;

        public RoutingPreset Result { get; private set; }

        public PresetForm(RoutingPreset preset, bool isNew) : base(L.T(isNew ? "routing.addPreset" : "routing.editTitle"))
        {
            _preset = preset;
            _custom = !RoutingSettings.IsBuiltIn(preset.Id);
            const int Pad = 20, Column = 230, Gap = 12, W = Column * 3 + Gap * 2;
            var mono = new Font("Consolas", 9.5f);
            int y = 16;
            Control top;
            if (_custom)
            {
                var nameLabel = new Label { Text = L.T("routing.name"), Font = Font, AutoSize = true, ForeColor = Theme.TextDim, Location = new Point(Pad, y) };
                _name.Text = preset.Name;
                _name.MaxLength = 80;
                _name.AccessibleName = L.T("routing.name");
                _name.Bounds = new Rectangle(Pad, nameLabel.Bottom + 4, 320, 32);
                Controls.AddRange(new Control[] { nameLabel, _name });
                top = _name;
            }
            else
            {
                top = Heading(RoutingForm.PresetName(preset), new Point(Pad - 2, y));
                Controls.Add(top);
            }
            y = top.Bottom + 14;

            var boxes = new[] { _direct, _proxy, _block };
            var texts = new[] { preset.Direct, preset.Proxy, preset.Block };
            var titles = new[] { L.T("routing.direct"), L.T("routing.proxy"), L.T("routing.block") };
            int boxTop = 0;
            for (int i = 0; i < 3; i++)
            {
                int x = Pad + i * (Column + Gap);
                var label = new Label { Text = titles[i], Font = Theme.Font(10.5f, FontStyle.Bold), AutoSize = true, Location = new Point(x, y) };
                boxes[i].EditorFont = mono;
                boxes[i].AccessibleName = titles[i];
                boxes[i].Text = (texts[i] ?? "").Replace("\r", "").Replace("\n", Environment.NewLine);
                boxes[i].Bounds = new Rectangle(x, label.Bottom + 6, Column, 250);
                boxes[i].TextChanged += (s, e) => _error.Text = "";
                Controls.AddRange(new Control[] { label, boxes[i] });
                boxTop = boxes[i].Bottom;
            }

            var syntax = Note(L.T("routing.syntax"), new Point(Pad, boxTop + 10), W);
            var priority = Note(L.T("routing.priority"), new Point(Pad, syntax.Bottom + 4), W);
            _error.Bounds = new Rectangle(Pad, priority.Bottom + 4, W, Note(L.T("routing.invalidRules") + ": " + new string('x', 120), Point.Empty, W, true).Height);
            _error.ForeColor = Theme.Bad;
            _error.AutoSize = false;

            _save = Theme.FlatButton(L.T("btn.save"), 110, primary: true);
            _cancel = Theme.FlatButton(L.T("btn.cancel"), 110);
            _reset = Theme.FlatButton(L.T("routing.resetPreset"), 150);
            _cancel.Location = new Point(Pad + W - _cancel.Width, _error.Bottom + 12);
            _save.Location = new Point(_cancel.Left - 8 - _save.Width, _cancel.Top);
            _reset.Location = new Point(Pad, _cancel.Top);
            _reset.Visible = !_custom;
            _reset.Click += (s, e) =>
            {
                var original = RoutingSettings.Original(preset.Id);
                for (int i = 0; i < 3; i++)
                    boxes[i].Text = original.Text(RoutingPreset.Actions[i]).Replace("\r", "").Replace("\n", Environment.NewLine);
            };
            _save.Click += (s, e) => TrySave();
            _cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

            ClientSize = new Size(W + Pad * 2, _cancel.Bottom + 18);
            Controls.AddRange(new Control[] { syntax, priority, _error, _save, _cancel, _reset });
        }

        void TrySave()
        {
            var result = new RoutingPreset
            {
                Id = _preset.Id,
                Name = _custom ? _name.Text.Trim() : "",
                Direct = _direct.Text.Replace("\r", "").Trim(),
                Proxy = _proxy.Text.Replace("\r", "").Trim(),
                Block = _block.Text.Replace("\r", "").Trim(),
            };
            if (_custom && result.Name.Length == 0) result.Name = L.T("routing.customPreset");
            try { RouteRules.Validate(result); }
            catch (RouteRuleException e)
            {
                _error.Text = L.T("routing.invalidRules") + ": " + e.Message;
                return;
            }
            Result = result;
            DialogResult = DialogResult.OK;
        }
    }
}
