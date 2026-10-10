using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Zarp.Core;

namespace Zarp.UI
{
    /// <summary>Основа дополнительных окон: тёмная тема, шрифт, Esc закрывает окно.</summary>
    abstract class DialogBase : Form
    {
        readonly Icon _icon;

        protected DialogBase(string title)
        {
            Text = title;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            Theme.DarkTitleBar(this);
            Icon = _icon = Theme.AppIcon(32, Theme.Off);
        }

        protected static Label Heading(string text, Point at) => new Label
        {
            Text = text, Font = Theme.Font(12f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = true, Location = at,
        };

        /// <summary>Подсказка или сообщение об ошибке: перенос строк, высота по тексту.</summary>
        protected Label Note(string text, Point at, int width, bool error = false) =>
            Theme.WrappedLabel(text, Font, error ? Theme.Bad : Theme.TextDim, at, width);

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _icon?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Поле ввода в тёмной теме: рамка в стиле остальных контролов вместо системной.</summary>
    sealed class DarkTextBox : Panel
    {
        readonly TextBox _box = new TextBox();

        public DarkTextBox(bool multiline)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            Padding = new Padding(10, multiline ? 8 : 6, 10, multiline ? 8 : 4);
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Theme.Panel;
            _box.ForeColor = Theme.Text;
            _box.Multiline = multiline;
            _box.AcceptsReturn = multiline;
            _box.WordWrap = multiline;
            _box.ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None;
            _box.Dock = DockStyle.Fill;
            _box.Font = Theme.Font(9.5f);
            _box.GotFocus += (s, e) => Invalidate();
            _box.LostFocus += (s, e) => Invalidate();
            _box.TextChanged += (s, e) => OnTextChanged(EventArgs.Empty);
            if (multiline) Theme.DarkScrollBars(_box);
            Controls.Add(_box);
            Height = multiline ? 100 : 32;
        }

        public override string Text
        {
            get => _box.Text;
            set => _box.Text = value;
        }

        public bool ReadOnly
        {
            get => _box.ReadOnly;
            set => _box.ReadOnly = value;
        }

        public int MaxLength
        {
            get => _box.MaxLength;
            set => _box.MaxLength = value;
        }

        /// <summary>Шрифт текста: для адресов и правил - моноширинный.</summary>
        public Font EditorFont
        {
            get => _box.Font;
            set => _box.Font = value;
        }

        public TextBox Editor => _box;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundRect(r, 8f))
            using (var fill = new SolidBrush(Theme.Panel))
            using (var pen = new Pen(_box.Focused ? Theme.Accent : Theme.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            _box.ForeColor = Enabled ? Theme.Text : Theme.TextDisabled;
            base.OnEnabledChanged(e);
        }
    }

    /// <summary>Таблица в тёмной теме: шапка рисуется сама (системная всегда светлая), строки целиком.</summary>
    sealed class DarkListView : ListView
    {
        public DarkListView()
        {
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            BorderStyle = BorderStyle.None;
            Font = Theme.Font(9f);
            OwnerDraw = true;
            DrawColumnHeader += DrawHeader;
            DrawItem += (s, e) => e.DrawDefault = true;
            DrawSubItem += (s, e) => e.DrawDefault = true;
            Theme.DarkScrollBars(this);
        }

        void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var bg = new SolidBrush(Theme.Back))
                e.Graphics.FillRectangle(bg, e.Bounds);
            using (var line = new Pen(Theme.Border))
                e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, Rectangle.Inflate(e.Bounds, -6, 0), Theme.TextDim, flags);
        }

        /// <summary>Растянуть колонку на оставшуюся ширину, чтобы справа от шапки не оставалось светлой полосы.</summary>
        public void FitColumn(int index)
        {
            if (Columns.Count <= index) return;
            int others = 0;
            for (int i = 0; i < Columns.Count; i++)
                if (i != index) others += Columns[i].Width;
            int w = ClientSize.Width - others;
            if (w > 80) Columns[index].Width = w;
        }
    }

    /// <summary>
    /// Кнопка выбора стран для фильтра стратегий. Меню с галочками не закрывается после выбора, чтобы можно было отметить
    /// несколько стран; «All» сбрасывает выбор.
    /// </summary>
    sealed class CountryFilterButton : DarkButton
    {
        // подписи стран в порядке StrategyFilter.Countries (ключи записаны явно, чтобы тесты видели, что они используются)
        static readonly string[] CountryKeys = { "country.all", "country.ru", "country.ir", "country.cn" };

        static string CountryName(string code) => L.T(CountryKeys[Array.IndexOf(StrategyFilter.Countries, code)]);

        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        string _value = StrategyFilter.All;

        public event EventHandler ValueChanged;

        public CountryFilterButton()
        {
            Height = 32;
            Margin = Padding.Empty;
            AccessibleRole = AccessibleRole.ComboBox;
            _menu.Renderer = new DarkMenuRenderer();
            _menu.ShowImageMargin = false;
            _menu.ShowCheckMargin = false;
            _menu.Padding = new Padding(3);
            // выбор страны не закрывает меню: за один заход можно отметить несколько
            _menu.Closing += (s, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
            _menu.Closed += (s, e) => Invalidate();
            Rebuild();
        }

        /// <summary>Выбранные страны: «all» или список через запятую.</summary>
        public string Value
        {
            get => _value;
            set
            {
                value = StrategyFilter.Normalize(value);
                if (value == _value) return;
                _value = value;
                RefreshText();
                SyncChecks();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Подписи на текущем языке: вызывается при создании и после смены языка.</summary>
        public void Rebuild()
        {
            _menu.Items.Clear();
            foreach (var code in StrategyFilter.Countries)
            {
                string country = code;
                var item = new ToolStripMenuItem(CountryName(country)) { Tag = country, CheckOnClick = false };
                item.Click += (s, e) => Value = StrategyFilter.Toggle(_value, country);
                _menu.Items.Add(item);
            }
            RefreshText();
            SyncChecks();
        }

        void RefreshText()
        {
            var selected = StrategyFilter.Selected(_value);
            string names = selected.Count == 0
                ? CountryName(StrategyFilter.All)
                : string.Join(", ", StrategyFilter.Countries.Where(selected.Contains).Select(CountryName));
            Text = L.T("filter.button", names);
            Invalidate();
        }

        void SyncChecks()
        {
            var selected = StrategyFilter.Selected(_value);
            foreach (ToolStripMenuItem item in _menu.Items)
            {
                string code = (string)item.Tag;
                item.Checked = code == StrategyFilter.All ? selected.Count == 0 : selected.Contains(code);
            }
        }

        /// <summary>Ширина, при которой влезает самая длинная подпись (все страны сразу).</summary>
        public int PreferredWidth()
        {
            string longest = L.T("filter.button", string.Join(", ", StrategyFilter.Countries.Where(c => c != StrategyFilter.All).Select(CountryName)));
            return TextRenderer.MeasureText(longest, Font).Width + Height + Math.Max(10, Height / 3) + 8;
        }

        protected override void DrawContent(Graphics g, Color color)
        {
            int inset = Math.Max(10, Height / 3);
            var textBounds = new Rectangle(inset, 0, Width - inset - Height, Height);
            TextRenderer.DrawText(g, Text, Font, textBounds, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            float cx = Width - Height / 2f, cy = (Height - 1) / 2f;
            float size = Height * 0.12f;
            using (var pen = new Pen(color, Math.Max(1.5f, Height / 22f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(cx - size, cy - size / 2), new PointF(cx, cy + size / 2), new PointF(cx + size, cy - size / 2) });
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (_menu.Visible) { _menu.Close(); return; }
            _menu.Font = Font;
            _menu.MinimumSize = new Size(Width, 0);
            int rowHeight = Math.Max(Height, Font.Height + 12);
            foreach (ToolStripMenuItem item in _menu.Items)
            {
                item.AutoSize = false;
                item.Size = new Size(Width - _menu.Padding.Horizontal, rowHeight);
            }
            SyncChecks();
            _menu.Show(this, new Point(0, Height + 4));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _menu.Dispose();
            base.Dispose(disposing);
        }
    }
}
