using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zarp.Core;

namespace Zarp.UI
{
    /// <summary>
    /// Базы GeoIP и GeoSite (V2Ray/Xray .dat): адрес источника, загрузка и обновление. Неудачное обновление оставляет прежнюю базу;
    /// у каждого адреса свои данные. Загрузка продолжается, даже если закрыть окно.
    /// </summary>
    sealed class GeoSourcesForm : DialogBase
    {
        sealed class Section
        {
            public string Kind;
            public Func<string> Saved;
            public Action<string> Save;
            public string Default;
            public DarkTextBox Url;
            public Label Status, Error;
            public DarkButton Download, Reset;
        }

        readonly Engine _engine;
        readonly List<Section> _sections = new List<Section>();

        public GeoSourcesForm(Engine engine) : base(L.T("geo.title"))
        {
            _engine = engine;
            const int Pad = 20, W = 640;
            var routing = engine.Config.Routing;
            var title = Heading(L.T("geo.title"), new Point(Pad - 2, 16));
            Controls.Add(title);
            int y = title.Bottom + 14;
            // одна высота для сообщений об ошибке: самое длинное из возможных, в две строки
            int errorHeight = Math.Max(Note(L.T("geo.failed"), Point.Empty, W).Height, Note(L.T("geo.cancelled"), Point.Empty, W).Height);

            foreach (var spec in new[]
            {
                new { Kind = "geoip", Name = "GeoIP", Saved = (Func<string>)(() => routing.GeoipUrl), Save = (Action<string>)(u => routing.GeoipUrl = u), Default = RoutingSettings.DefaultGeoipUrl },
                new { Kind = "geosite", Name = "GeoSite", Saved = (Func<string>)(() => routing.GeositeUrl), Save = (Action<string>)(u => routing.GeositeUrl = u), Default = RoutingSettings.DefaultGeositeUrl },
            })
            {
                var section = new Section { Kind = spec.Kind, Saved = spec.Saved, Save = spec.Save, Default = spec.Default };
                var name = new Label { Text = spec.Name, Font = Theme.Font(10.5f, FontStyle.Bold), AutoSize = true, Location = new Point(Pad, y) };
                var caption = new Label { Text = L.T("geo.source"), Font = Font, AutoSize = true, ForeColor = Theme.TextDim, Location = new Point(Pad, name.Bottom + 4) };
                section.Url = new DarkTextBox(false) { Bounds = new Rectangle(Pad, caption.Bottom + 4, W, 32), Text = spec.Saved(), MaxLength = 2048 };
                section.Url.AccessibleName = spec.Name + " " + L.T("geo.source");
                section.Url.EditorFont = new Font("Consolas", 9f);
                section.Status = new Label { Bounds = new Rectangle(Pad, section.Url.Bottom + 6, W, 20), AutoEllipsis = true, ForeColor = Theme.TextDim, AutoSize = false };
                section.Error = new Label { Bounds = new Rectangle(Pad, section.Status.Bottom + 2, W, errorHeight), ForeColor = Theme.Bad, AutoSize = false };
                section.Download = Theme.FlatButton(L.T("geo.update"), 130, primary: false);
                section.Download.Width = Math.Max(section.Download.Width, Theme.FlatButton(L.T("geo.download"), 130).Width);
                section.Download.Location = new Point(Pad, section.Error.Bottom + 4);
                section.Reset = Theme.FlatButton(L.T("geo.resetSource"), 160);
                section.Reset.Location = new Point(section.Download.Right + 8, section.Download.Top);
                var captured = section;
                section.Download.Click += (s, e) => StartDownload(captured);
                section.Reset.Click += (s, e) => captured.Url.Text = captured.Default;
                section.Url.TextChanged += (s, e) => UrlChanged(captured);
                Controls.AddRange(new Control[] { name, caption, section.Url, section.Status, section.Error, section.Download, section.Reset });
                _sections.Add(section);
                y = section.Download.Bottom + 18;
            }

            var format = Note(L.T("geo.format"), new Point(Pad, y - 4), W);
            var reconnect = Note(L.T("settings.reconnect"), new Point(Pad, format.Bottom + 2), W);
            var close = Theme.FlatButton(L.T("btn.close"), 110);
            close.Location = new Point(Pad + W - close.Width, reconnect.Bottom + 12);
            close.Click += (s, e) => Close();
            ClientSize = new Size(W + Pad * 2, close.Bottom + 18);
            Controls.AddRange(new Control[] { format, reconnect, close });

            engine.Geo.Changed += OnGeoChanged;
            foreach (var section in _sections) Refresh(section);
        }

        string Typed(Section s) => s.Url.Text.Trim();

        /// <summary>Нужный адрес записывается сразу, как только он стал верным: отдельной кнопки «Сохранить» нет.</summary>
        void UrlChanged(Section s)
        {
            string url = Typed(s);
            if (RoutingSettings.ValidSource(url) && url != s.Saved())
            {
                s.Save(url);
                _engine.Config.Save();
            }
            Refresh(s);
        }

        void StartDownload(Section s)
        {
            string url = Typed(s);
            if (!RoutingSettings.ValidSource(url) || _engine.Geo.AnyDownloading) return;
            if (url != s.Saved()) { s.Save(url); _engine.Config.Save(); }
            // без await: загрузка живёт в GeoData и не зависит от окна, итог виден по состоянию
            var ignored = _engine.Geo.DownloadAsync(s.Kind, url);
            Refresh(s);
        }

        void OnGeoChanged()
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed) foreach (var s in _sections) Refresh(s); })); }
            catch (InvalidOperationException) { }
        }

        void Refresh(Section s)
        {
            string url = Typed(s);
            bool valid = RoutingSettings.ValidSource(url);
            var state = valid ? _engine.Geo.State(s.Kind, url) : new GeoFileState();
            if (!valid) { s.Status.ForeColor = Theme.Bad; s.Status.Text = L.T("geo.invalidUrl"); }
            else
            {
                s.Status.ForeColor = Theme.TextDim;
                if (state.Downloading) s.Status.Text = L.T("geo.downloading", (state.Bytes / 1024).ToString(CultureInfo.CurrentCulture));
                else if (state.Exists)
                    s.Status.Text = L.T("geo.ready", state.Categories, state.UpdatedAt.ToString("g", CultureInfo.CurrentCulture));
                else if (s.Kind == "geoip" && url == RoutingSettings.DefaultGeoipUrl) s.Status.Text = L.T("geo.bundled");
                else s.Status.Text = L.T("geo.missing");
            }
            s.Error.Text = valid ? state.Error ?? "" : "";
            s.Download.Text = L.T(state.Exists ? "geo.update" : "geo.download");
            s.Download.Enabled = valid && !_engine.Geo.AnyDownloading;
            s.Reset.Visible = url != s.Default;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _engine.Geo.Changed -= OnGeoChanged;
            base.Dispose(disposing);
        }
    }
}
