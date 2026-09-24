using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Zarp.UI
{
    /// <summary>Кнопка в тёмной теме: скруглённая, с нормальным видом в неактивном состоянии.</summary>
    class DarkButton : Button
    {
        bool _hover, _down;

        /// <summary>Акцентная (оранжевая) кнопка основного действия.</summary>
        public bool Primary { get; set; }

        public DarkButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Font = Theme.Font(9f);
            Height = 34;
            Margin = new Padding(0, 0, 8, 0);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Back);

            Color bg, fg, border;
            if (!Enabled)
            {
                bg = Theme.Back; fg = Theme.TextDisabled; border = Theme.Border;
            }
            else if (Primary)
            {
                bg = _down ? ControlPaint.Dark(Theme.Accent, 0.05f) : _hover ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent;
                fg = Theme.OnAccent; border = bg;
            }
            else
            {
                bg = _down ? Theme.Border : _hover ? Theme.PanelHover : Theme.Panel;
                fg = Theme.Text; border = Focused && ShowFocusCues ? Theme.Accent : _hover ? Theme.BorderHover : Theme.Border;
            }

            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundRect(r, Math.Min(8f, Height / 3f)))
            using (var br = new SolidBrush(bg))
            using (var pen = new Pen(border))
            {
                g.FillPath(br, path);
                g.DrawPath(pen, path);
            }
            DrawContent(g, fg);
        }

        protected virtual void DrawContent(Graphics g, Color color)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    /// <summary>Шестерёнка рисуется по центру кнопки и не зависит от метрик шрифта.</summary>
    sealed class SettingsButton : DarkButton
    {
        static readonly double[] ToothAngles = { -22.5, -12.0, 12.0, 22.5 };

        public SettingsButton()
        {
            Size = new Size(40, 40);
            AccessibleName = "Настройки";
        }

        protected override void DrawContent(Graphics g, Color color)
        {
            float scale = Math.Min(Width, Height) / 40f;
            float cx = (Width - 1) / 2f, cy = (Height - 1) / 2f;
            var points = new PointF[32];
            for (int tooth = 0; tooth < 8; tooth++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    double angle = (tooth * 45 + ToothAngles[corner]) * Math.PI / 180;
                    float radius = (corner == 0 || corner == 3 ? 6.8f : 9f) * scale;
                    points[tooth * 4 + corner] = new PointF(cx + radius * (float)Math.Cos(angle), cy + radius * (float)Math.Sin(angle));
                }
            }
            using (var pen = new Pen(color, 1.6f * scale) { LineJoin = LineJoin.Round })
            {
                g.DrawPolygon(pen, points);
                float hole = 3f * scale;
                g.DrawEllipse(pen, cx - hole, cy - hole, hole * 2, hole * 2);
            }
        }
    }

    /// <summary>Выбор из короткого списка в стиле остальных кнопок, включая раскрытое меню.</summary>
    sealed class DarkSelect : DarkButton
    {
        readonly string[] _items;
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        int _selectedIndex = -1;

        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (value < 0 || value >= _items.Length) throw new ArgumentOutOfRangeException(nameof(value));
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                Text = _items[value];
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        public DarkSelect(params string[] items)
        {
            _items = items;
            Width = 240;
            Height = 32;
            Margin = Padding.Empty;
            AccessibleRole = AccessibleRole.ComboBox;
            _menu.Renderer = new SelectMenuRenderer();
            _menu.ShowImageMargin = false;
            _menu.ShowCheckMargin = false;
            _menu.Padding = new Padding(3);
            for (int i = 0; i < items.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(items[i]);
                item.Click += (s, e) => SelectedIndex = index;
                _menu.Items.Add(item);
            }
            _menu.Closed += (s, e) => Invalidate();
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
            for (int i = 0; i < _menu.Items.Count; i++)
            {
                var item = (ToolStripMenuItem)_menu.Items[i];
                item.AutoSize = false;
                item.Size = new Size(Width - _menu.Padding.Horizontal, rowHeight);
                item.Checked = i == _selectedIndex;
            }
            _menu.Show(this, new Point(0, Height + 4));
            if (_selectedIndex >= 0) _menu.Items[_selectedIndex].Select();
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down)) PerformClick();
            else if (e.KeyCode == Keys.Up) SelectedIndex = Math.Max(0, SelectedIndex - 1);
            else if (e.KeyCode == Keys.Down) SelectedIndex = Math.Min(_items.Length - 1, SelectedIndex + 1);
            else if (e.KeyCode == Keys.Home) SelectedIndex = 0;
            else if (e.KeyCode == Keys.End) SelectedIndex = _items.Length - 1;
            else { base.OnKeyDown(e); return; }
            e.Handled = e.SuppressKeyPress = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _menu.Dispose();
            base.Dispose(disposing);
        }

        sealed class SelectMenuRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var brush = new SolidBrush(Theme.Panel)) e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var pen = new Pen(Theme.BorderHover))
                    e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                using (var brush = new SolidBrush(e.Item.Selected ? Theme.PanelHover : Theme.Panel))
                    e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
                if (e.Item is ToolStripMenuItem item && item.Checked)
                    using (var brush = new SolidBrush(Theme.Accent))
                        e.Graphics.FillRectangle(brush, 0, item.Height / 4, 2, item.Height / 2);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Theme.Text;
                base.OnRenderItemText(e);
            }
        }
    }

    /// <summary>Переключатель вместо мелкого системного чекбокса.</summary>
    sealed class ToggleSwitch : CheckBox
    {
        bool _hover;

        public ToggleSwitch(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            Font = Theme.Font(9.5f);
            Cursor = Cursors.Hand;
            AutoSize = true;
            Margin = new Padding(0, 0, 0, 6);
        }

        int TrackH => (int)Math.Round(Font.Height * 1.15);
        int TrackW => (int)Math.Round(TrackH * 1.8);
        int Gap => Font.Height;

        public override Size GetPreferredSize(Size proposed)
        {
            var text = TextRenderer.MeasureText(Text, Font);
            return new Size(TrackW + Gap + text.Width + 2, Math.Max(TrackH, text.Height) + 8);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Back);

            float th = TrackH, tw = TrackW;
            var track = new RectangleF(0.5f, (Height - th) / 2f, tw, th);
            Color trackColor = Checked
                ? (_hover ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent)
                : (_hover ? Theme.BorderHover : Theme.Off);
            if (!Enabled) trackColor = Theme.Border;
            using (var path = Theme.RoundRect(track, th / 2f))
            using (var br = new SolidBrush(trackColor))
                g.FillPath(br, path);

            float pad = th * 0.16f, knob = th - pad * 2;
            float kx = Checked ? track.Right - pad - knob : track.X + pad;
            using (var br = new SolidBrush(Checked ? Theme.OnAccentKnob : Theme.Text))
                g.FillEllipse(br, kx, track.Y + pad, knob, knob);

            var textRect = new Rectangle((int)(tw + Gap), 0, Width - (int)(tw + Gap), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? Theme.Text : Theme.TextDisabled,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Числовое поле «− значение +» в тёмной теме вместо NumericUpDown.</summary>
    sealed class NumberBox : Control
    {
        int _value, _hoverPart; // -1 минус, 1 плюс, 0 нет

        public int Minimum { get; set; }
        public int Maximum { get; set; } = 100;
        public event EventHandler ValueChanged;

        public NumberBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = Theme.Font(9.5f);
            Size = new Size(116, 32);
        }

        public int Value
        {
            get => _value;
            set
            {
                int v = Math.Max(Minimum, Math.Min(Maximum, value));
                if (v == _value) return;
                _value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        Rectangle MinusRect => new Rectangle(0, 0, Height, Height);
        Rectangle PlusRect => new Rectangle(Width - Height, 0, Height, Height);

        int PartAt(Point p) => MinusRect.Contains(p) ? -1 : PlusRect.Contains(p) ? 1 : 0;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int part = PartAt(e.Location);
            if (part != _hoverPart) { _hoverPart = part; Cursor = part != 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hoverPart = 0; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            Value += PartAt(e.Location);
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Value += Math.Sign(e.Delta);
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys k) => k == Keys.Up || k == Keys.Down || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Right) Value++;
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Left) Value--;
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Back);

            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundRect(r, 8f))
            {
                using (var br = new SolidBrush(Theme.Panel)) g.FillPath(br, path);
                g.SetClip(path);
                foreach (var part in new[] { -1, 1 })
                {
                    if (_hoverPart != part) continue;
                    using (var br = new SolidBrush(Theme.PanelHover))
                        g.FillRectangle(br, part < 0 ? MinusRect : PlusRect);
                }
                g.ResetClip();
                using (var pen = new Pen(Focused ? Theme.Accent : Theme.Border)) g.DrawPath(pen, path);
            }

            // разделители и знаки
            using (var sep = new Pen(Theme.Border))
            {
                g.DrawLine(sep, MinusRect.Right, 6, MinusRect.Right, Height - 7);
                g.DrawLine(sep, PlusRect.Left, 6, PlusRect.Left, Height - 7);
            }
            float s = Height * 0.18f;
            float cy = Height / 2f, mx = MinusRect.Left + MinusRect.Width / 2f, px = PlusRect.Left + PlusRect.Width / 2f;
            // на границе диапазона знак гаснет
            using (var minus = new Pen(Value > Minimum ? Theme.Text : Theme.TextDisabled, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(minus, mx - s, cy, mx + s, cy);
            using (var plus = new Pen(Value < Maximum ? Theme.Text : Theme.TextDisabled, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(plus, px - s, cy, px + s, cy);
                g.DrawLine(plus, px, cy - s, px, cy + s);
            }
            var valRect = new Rectangle(MinusRect.Right, 0, PlusRect.Left - MinusRect.Right, Height);
            TextRenderer.DrawText(g, Value.ToString(), Font, valRect, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
