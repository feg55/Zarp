using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Zarp.UI
{
    /// <summary>Большая круглая кнопка питания с кольцом состояния и анимацией ожидания.</summary>
    sealed class PowerButton : Control
    {
        public enum Look { Off, Busy, On }

        Look _look = Look.Off;
        bool _hover, _down;
        float _angle;
        readonly Timer _anim = new Timer { Interval = 16 };

        public PowerButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            _anim.Tick += (s, e) => { _angle = (_angle + 5f) % 360f; Invalidate(); };
        }

        public Look State
        {
            get => _look;
            set
            {
                if (_look == value) return;
                _look = value;
                _anim.Enabled = value == Look.Busy;
                Invalidate();
            }
        }

        Color RingColor => _look == Look.On ? Theme.Accent : _look == Look.Busy ? Theme.Busy : Theme.Off;

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Back);

            float size = Math.Min(Width, Height) - 8;
            var rc = new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size);
            float ring = size * 0.055f;

            // мягкое свечение во включённом состоянии
            if (_look == Look.On)
            {
                for (int i = 6; i >= 1; i--)
                {
                    var glow = RectangleF.Inflate(rc, -ring + i * 1.5f, -ring + i * 1.5f);
                    using (var pen = new Pen(Color.FromArgb(14, Theme.Accent), i * 2f))
                        g.DrawEllipse(pen, glow);
                }
            }

            // кольцо
            var ringRc = RectangleF.Inflate(rc, -ring / 2f, -ring / 2f);
            using (var pen = new Pen(Color.FromArgb(_look == Look.Busy ? 60 : 255, RingColor), ring))
                g.DrawEllipse(pen, ringRc);
            if (_look == Look.Busy)
            {
                using (var pen = new Pen(Theme.Busy, ring) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, ringRc, _angle, 90);
            }

            // диск
            var disc = RectangleF.Inflate(rc, -ring * 2.2f, -ring * 2.2f);
            Color fill = _down ? Theme.Border : _hover ? Theme.PanelHover : Theme.Panel;
            using (var br = new SolidBrush(fill))
                g.FillEllipse(br, disc);

            // значок питания
            float icon = disc.Width * 0.36f;
            var ic = new RectangleF(disc.X + (disc.Width - icon) / 2f, disc.Y + (disc.Height - icon) / 2f + icon * 0.04f, icon, icon);
            Color iconColor = _look == Look.On ? Theme.Accent : _look == Look.Busy ? Theme.Busy : Theme.TextDim;
            using (var pen = new Pen(iconColor, icon * 0.11f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, ic, -60, 300);
                g.DrawLine(pen, ic.X + ic.Width / 2f, ic.Y - icon * 0.12f, ic.X + ic.Width / 2f, ic.Y + ic.Height * 0.42f);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }
}
