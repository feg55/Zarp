using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zarp.Core;

namespace Zarp.UI
{
    /// <summary>
    /// Через собственный сервер идут только выбранные программы, остальные подключаются как обычно. Программу определяет
    /// путь к exe. Если режим включён, а выбрано ноль существующих программ, подключение не начнётся: пустой выбор нельзя
    /// молча превращать в «все программы».
    /// </summary>
    sealed class AppsForm : DialogBase
    {
        /// <summary>Источник списка программ. Подменяется в тестах: настоящий просмотр процессов и ярлыков занимает секунды.</summary>
        internal static Func<List<AppEntry>> Source = InstalledApps.Enumerate;

        readonly Engine _engine;
        readonly ToggleSwitch _enable = new ToggleSwitch(L.T("apps.enable"));
        readonly DarkTextBox _search = new DarkTextBox(false);
        readonly DarkListView _list = new DarkListView();
        readonly Label _summary = new Label();
        readonly DarkButton _add, _refresh, _close;
        List<AppEntry> _found = new List<AppEntry>();
        bool _loaded;

        AppConfig Config => _engine.Config;

        public AppsForm(Engine engine) : base(L.T("apps.title"))
        {
            _engine = engine;
            const int Pad = 20, W = 720;
            var title = Heading(L.T("apps.title"), new Point(Pad - 2, 16));
            _enable.Location = new Point(Pad, title.Bottom + 12);
            int y = _enable.Bottom + 6;
            var parts = new List<Control> { title, _enable };
            if (!engine.ProxyMode)
            {
                var onlyProxy = Note(L.T("apps.onlyProxy"), new Point(Pad, y), W);
                parts.Add(onlyProxy);
                y = onlyProxy.Bottom + 6;
            }
            var hint = Note(L.T("apps.hint"), new Point(Pad, y), W);
            parts.Add(hint);
            y = hint.Bottom + 4;
            var reconnect = Note(L.T("settings.reconnect"), new Point(Pad, y), W);
            parts.Add(reconnect);
            y = reconnect.Bottom + 8;

            _summary.Bounds = new Rectangle(Pad, y, W, 22);
            _summary.AutoSize = false;
            _summary.AutoEllipsis = true;
            y = _summary.Bottom + 4;
            var searchLabel = new Label { Text = L.T("apps.search"), Font = Font, AutoSize = true, ForeColor = Theme.TextDim, Location = new Point(Pad, y) };
            _search.Bounds = new Rectangle(Pad, searchLabel.Bottom + 4, W, 32);
            _search.AccessibleName = L.T("apps.search");
            _search.TextChanged += (s, e) => FillList();

            _list.Columns.Add("", 28);
            _list.Columns.Add(L.T("apps.colName"), 240);
            _list.Columns.Add(L.T("apps.colPath"), 440);
            _list.MultiSelect = false;
            _list.Bounds = new Rectangle(Pad, _search.Bottom + 8, W, 250);
            _list.Resize += (s, e) => _list.FitColumn(2);
            _list.DoubleClick += (s, e) => ToggleSelected();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Space) { ToggleSelected(); e.Handled = e.SuppressKeyPress = true; }
            };
            _add = Theme.FlatButton(L.T("apps.add"), 150);
            _refresh = Theme.FlatButton(L.T("apps.refresh"), 110);
            _close = Theme.FlatButton(L.T("btn.close"), 110);
            _add.Location = new Point(Pad, _list.Bottom + 10);
            _refresh.Location = new Point(_add.Right + 8, _add.Top);
            _close.Location = new Point(Pad + W - _close.Width, _add.Top);
            _add.Click += (s, e) => AddProgram();
            _refresh.Click += (s, e) => LoadApps();
            _close.Click += (s, e) => Close();

            ClientSize = new Size(W + Pad * 2, _close.Bottom + 18);
            parts.AddRange(new Control[] { _summary, searchLabel, _search, _list, _add, _refresh, _close });
            Controls.AddRange(parts.ToArray());

            _enable.Checked = Config.PerAppProxy;
            _enable.CheckedChanged += (s, e) =>
            {
                Config.PerAppProxy = _enable.Checked;
                Config.Save();
                UpdateSummary();
            };
            UpdateSummary();
            _list.FitColumn(2);
            LoadApps();
        }

        HashSet<string> Selected() => new HashSet<string>(Config.ProxyApps, StringComparer.OrdinalIgnoreCase);

        /// <summary>Поиск программ идёт не в потоке окна: просмотр процессов и ярлыков занимает секунды.</summary>
        async void LoadApps()
        {
            _refresh.Enabled = false;
            _loaded = false;
            UpdateSummary();
            List<AppEntry> found;
            try { found = await Task.Run(Source); }
            catch (Exception e)
            {
                Log.Write(L.T("log.error", e.Message));
                found = new List<AppEntry>();
            }
            if (IsDisposed) return;
            _found = found;
            _loaded = true;
            _refresh.Enabled = true;
            FillList();
        }

        /// <summary>Найденные программы плюс выбранные, которых в списке нет (добавленные вручную или удалённые с диска).</summary>
        List<AppEntry> AllEntries()
        {
            var list = _found.ToList();
            var known = new HashSet<string>(list.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var path in Config.ProxyApps.Where(p => !known.Contains(p)))
                list.Add(File.Exists(path) ? InstalledApps.Describe(path) : new AppEntry { Path = path, Name = Path.GetFileNameWithoutExtension(path) + " " + L.T("apps.missing") });
            return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        void FillList()
        {
            string query = _search.Text.Trim();
            var chosen = Selected();
            string keep = _list.SelectedItems.Count == 1 ? ((AppEntry)_list.SelectedItems[0].Tag).Path : null;
            _list.BeginUpdate();
            _list.Items.Clear();
            var visible = AllEntries().Where(a => query.Length == 0 || a.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                                  a.Path.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            foreach (var app in visible)
            {
                bool on = chosen.Contains(app.Path);
                var item = new ListViewItem(new[] { on ? "✔" : "", app.Name, app.Path }) { Tag = app, UseItemStyleForSubItems = false, ToolTipText = app.Path };
                if (on) item.SubItems[1].Font = Theme.Font(9f, FontStyle.Bold);
                foreach (ListViewItem.ListViewSubItem sub in item.SubItems) sub.BackColor = Theme.Panel;
                item.Selected = keep != null && string.Equals(app.Path, keep, StringComparison.OrdinalIgnoreCase);
                _list.Items.Add(item);
            }
            _list.ShowItemToolTips = true;
            _list.EndUpdate();
            UpdateSummary();
        }

        void ToggleSelected()
        {
            if (_list.SelectedItems.Count != 1) return;
            string path = ((AppEntry)_list.SelectedItems[0].Tag).Path;
            var chosen = Selected();
            if (!chosen.Remove(path)) chosen.Add(path);
            Config.ProxyApps = Config.ProxyApps.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Contains(path)) Config.ProxyApps.Add(path);
            Config.Save();
            FillList();
        }

        void AddProgram()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = L.T("apps.pickTitle"), Filter = L.T("apps.pickFilter") + "|*.exe", CheckFileExists = true, Multiselect = true,
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (var path in dialog.FileNames)
                    if (!Selected().Contains(path)) Config.ProxyApps.Add(path);
                Config.Save();
                if (!_found.Any(a => dialog.FileNames.Any(f => string.Equals(f, a.Path, StringComparison.OrdinalIgnoreCase))))
                    _found.AddRange(dialog.FileNames.Select(InstalledApps.Describe));
                FillList();
            }
        }

        void UpdateSummary()
        {
            int existing = Config.ProxyApps.Count(File.Exists);
            bool problem = Config.PerAppProxy && existing == 0;
            _summary.ForeColor = problem ? Theme.Bad : Theme.TextDim;
            _summary.Text = !_loaded ? L.T("apps.loading") : existing == 0 ? L.T("apps.empty") : L.T("apps.selected", existing);
        }
    }
}
