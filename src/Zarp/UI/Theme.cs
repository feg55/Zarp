using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Zarp.UI
{
    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(18, 20, 25);
        public static readonly Color Panel = Color.FromArgb(27, 30, 37);
        public static readonly Color PanelHover = Color.FromArgb(37, 41, 50);
        public static readonly Color Border = Color.FromArgb(46, 50, 60);
        // не чисто белый - на тёмном фоне он режет глаз
        public static readonly Color Text = Color.FromArgb(196, 200, 208);
        public static readonly Color TextDim = Color.FromArgb(128, 134, 146);
        public static readonly Color TextDisabled = Color.FromArgb(78, 83, 94);
        public static readonly Color BorderHover = Color.FromArgb(64, 69, 81);
        public static readonly Color OnAccent = Color.FromArgb(30, 20, 12);      // тёмный текст на оранжевом
        public static readonly Color OnAccentKnob = Color.FromArgb(250, 246, 242);
        public static readonly Color Accent = Color.FromArgb(244, 129, 32);   // оранжевый Cloudflare
        public static readonly Color Busy = Color.FromArgb(80, 150, 255);
        public static readonly Color Ok = Color.FromArgb(64, 196, 120);
        public static readonly Color Bad = Color.FromArgb(232, 84, 84);
        public static readonly Color Off = Color.FromArgb(70, 75, 88);

        public static Font Font(float size, FontStyle style = FontStyle.Regular) => new Font("Segoe UI", size, style);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        /// <summary>Тёмный системный заголовок окна (Windows 10 20H1+ / 11).</summary>
        public static void DarkTitleBar(Form f)
        {
            f.HandleCreated += (s, e) =>
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, 4) != 0)
                    DwmSetWindowAttribute(f.Handle, 19 /* то же на старых сборках Windows 10 */, ref on, 4);
                int caption = ColorTranslator.ToWin32(Back);
                DwmSetWindowAttribute(f.Handle, 35 /* DWMWA_CAPTION_COLOR, Windows 11 */, ref caption, 4);
            };
        }

        /// <summary>Тёмные полосы прокрутки у стандартных контролов.</summary>
        public static void DarkScrollBars(Control c)
        {
            c.HandleCreated += (s, e) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        }

        /// <summary>Тёмная кнопка (см. DarkButton).</summary>
        public static DarkButton FlatButton(string text, int width = 120, bool primary = false) =>
            new DarkButton { Text = text, Width = width, Primary = primary };

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Иконка приложения: оранжевое «облако-щит» с молнией. Рисуется кодом, чтобы не таскать файлы.</summary>
        public static Bitmap AppImage(int size, Color color)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 32f;
                using (var br = new SolidBrush(color))
                    g.FillEllipse(br, 1 * s, 1 * s, 30 * s, 30 * s);
                using (var br = new SolidBrush(Color.White))
                {
                    var bolt = new[]
                    {
                        new PointF(18 * s, 5 * s), new PointF(9 * s, 18 * s), new PointF(15 * s, 18 * s),
                        new PointF(13 * s, 27 * s), new PointF(23 * s, 13 * s), new PointF(17 * s, 13 * s),
                    };
                    g.FillPolygon(br, bolt);
                }
            }
            return bmp;
        }

        public static Icon AppIcon(int size, Color color)
        {
            using (var bmp = AppImage(size, color))
                return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
