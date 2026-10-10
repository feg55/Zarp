using System;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using Zarp.Core;

namespace Zarp.UI
{
    /// <summary>
    /// Эндпоинты WARP и собственный сервер вместо WARP. Изменения применяются при следующем подключении,
    /// а прежние результаты проверок сбрасываются: они относились к другому серверу.
    /// </summary>
    sealed class ConnectionForm : DialogBase
    {
        readonly Engine _engine;
        readonly ToggleSwitch _useProxy = new ToggleSwitch(L.T("proxy.enable"));
        readonly ToggleSwitch _ipv6 = new ToggleSwitch(L.T("proxy.ipv6"));
        readonly DarkTextBox _link = new DarkTextBox(true);
        readonly DarkTextBox _dns = new DarkTextBox(false);
        readonly DarkTextBox _endpoints = new DarkTextBox(true);
        readonly Label _linkStatus = new Label();
        readonly Label _endpointStatus = new Label();
        readonly Panel _serverPanel = new Panel();
        readonly Panel _warpPanel = new Panel();
        readonly DarkButton _reset, _save, _close;
        readonly int _statusHeight;
        bool _loading;

        public ConnectionForm(Engine engine) : base(L.T("endpoint.title"))
        {
            _engine = engine;
            const int Pad = 20, W = 600;
            var mono = new Font("Consolas", 9.5f);

            var title = Heading(L.T("endpoint.title"), new Point(Pad - 2, 16));
            _useProxy.Location = new Point(Pad, title.Bottom + 12);

            // ---- собственный сервер
            var proxyHint = Note(L.T("proxy.hint"), new Point(0, 0), W);
            _link.EditorFont = mono;
            _link.AccessibleName = L.T("proxy.link");
            _link.Bounds = new Rectangle(0, proxyHint.Bottom + 8, W, 92);
            _link.MaxLength = 8192;
            _statusHeight = Math.Max(Note(L.T("proxy.invalid"), Point.Empty, W).Height, Note(L.T("proxy.dnsInvalid"), Point.Empty, W).Height);
            _linkStatus.Bounds = new Rectangle(0, _link.Bottom + 6, W, _statusHeight);
            _linkStatus.AutoSize = false;
            _linkStatus.AutoEllipsis = true;
            _ipv6.Location = new Point(0, _linkStatus.Bottom + 6);
            // шрифт задан явно: ширина считается сейчас, до того как метка попадёт на форму и унаследует его
            var dnsLabel = new Label { Text = L.T("proxy.dns"), Font = Font, AutoSize = true, ForeColor = Theme.Text };
            dnsLabel.Location = new Point(0, 0);
            _dns.AccessibleName = L.T("proxy.dns");
            _dns.EditorFont = mono;
            _dns.Size = new Size(170, 32);
            _dns.Location = new Point(W - _dns.Width, _ipv6.Top - 1);
            dnsLabel.Location = new Point(_dns.Left - 10 - dnsLabel.PreferredWidth, _ipv6.Top + 6);
            _serverPanel.Size = new Size(W, Math.Max(_ipv6.Bottom, _dns.Bottom) + 2);
            _serverPanel.Controls.AddRange(new Control[] { proxyHint, _link, _linkStatus, _ipv6, dnsLabel, _dns });

            // ---- эндпоинты WARP
            var warpTitle = new Label { Text = L.T("endpoint.warp"), Font = Theme.Font(10.5f, FontStyle.Bold), AutoSize = true, Location = Point.Empty };
            var warpHint = Note(L.T("endpoint.hint"), new Point(0, warpTitle.PreferredHeight + 6), W);
            _endpoints.EditorFont = mono;
            _endpoints.AccessibleName = L.T("endpoint.warp");
            _endpoints.Bounds = new Rectangle(0, warpHint.Bottom + 8, W, 128);
            _endpointStatus.Bounds = new Rectangle(0, _endpoints.Bottom + 6, W, Note(L.T("endpoint.invalid"), Point.Empty, W).Height);
            _endpointStatus.AutoSize = false;
            _reset = Theme.FlatButton(L.T("endpoint.reset"), 160);
            _reset.Location = new Point(0, _endpointStatus.Bottom + 6);
            _reset.Click += (s, e) => _endpoints.Text = "";
            _warpPanel.Size = new Size(W, _reset.Bottom + 2);
            _warpPanel.Controls.AddRange(new Control[] { warpTitle, warpHint, _endpoints, _endpointStatus, _reset });

            int panelHeight = Math.Max(_serverPanel.Height, _warpPanel.Height);
            _serverPanel.Location = _warpPanel.Location = new Point(Pad, _useProxy.Bottom + 10);
            _serverPanel.Height = _warpPanel.Height = panelHeight;

            var applies = Note(L.T("endpoint.applies"), new Point(Pad, _serverPanel.Bottom + 8), W);
            _save = Theme.FlatButton(L.T("btn.save"), 110, primary: true);
            _close = Theme.FlatButton(L.T("btn.close"), 110);
            _close.Location = new Point(Pad + W - _close.Width, applies.Bottom + 14);
            _save.Location = new Point(_close.Left - 8 - _save.Width, _close.Top);
            _save.Click += (s, e) => Save();
            _close.Click += (s, e) => Close();

            ClientSize = new Size(W + Pad * 2, _close.Bottom + 18);
            Controls.AddRange(new Control[] { title, _useProxy, _serverPanel, _warpPanel, applies, _save, _close });

            LoadValues();
            _useProxy.CheckedChanged += (s, e) => Changed();
            _ipv6.CheckedChanged += (s, e) => Changed();
            _link.TextChanged += (s, e) => Changed();
            _dns.TextChanged += (s, e) => Changed();
            _endpoints.TextChanged += (s, e) => Changed();
            engine.Changed += OnEngineChanged;
            Changed();
        }

        void LoadValues()
        {
            _loading = true;
            var c = _engine.Config;
            _useProxy.Checked = c.UseProxy;
            _link.Text = c.ProxyUri;
            _ipv6.Checked = c.ProxyIpv6;
            _dns.Text = c.ProxyDns;
            _endpoints.Text = c.CustomEndpoints;
            _loading = false;
        }

        static bool DnsValid(string text)
        {
            var parts = (text ?? "").Split(',').Select(p => p.Trim()).ToList();
            return parts.Count > 0 && parts.All(p => IPAddress.TryParse(p, out _));
        }

        /// <summary>Пересчитать подсказки и доступность кнопки сохранения.</summary>
        void Changed()
        {
            if (_loading) return;
            bool proxy = _useProxy.Checked;
            _serverPanel.Visible = proxy;
            _warpPanel.Visible = !proxy;

            ProxyProfile.TryParse(_link.Text, out var profile);
            bool dnsOk = DnsValid(_dns.Text);
            if (profile != null && dnsOk)
            {
                _linkStatus.ForeColor = Theme.TextDim;
                _linkStatus.Text = profile.Title + ": " + profile.Endpoint;
            }
            else
            {
                _linkStatus.ForeColor = Theme.Bad;
                _linkStatus.Text = _link.Text.Trim().Length == 0 ? "" : profile == null ? L.T("proxy.invalid") : L.T("proxy.dnsInvalid");
            }

            bool endpointsOk = EndpointParser.TryParse(_endpoints.Text, out _);
            _endpointStatus.ForeColor = Theme.Bad;
            _endpointStatus.Text = endpointsOk ? "" : L.T("endpoint.invalid");
            _reset.Visible = _endpoints.Text.Trim().Length > 0;

            var c = _engine.Config;
            bool dirty = _useProxy.Checked != c.UseProxy || _link.Text.Trim() != c.ProxyUri.Trim() || _ipv6.Checked != c.ProxyIpv6
                         || _dns.Text.Trim() != c.ProxyDns || _endpoints.Text.Trim() != c.CustomEndpoints.Trim();
            _save.Enabled = !_engine.IsBusy && dirty && endpointsOk && (!proxy || (profile != null && dnsOk));
        }

        void Save()
        {
            if (!_save.Enabled) return;
            var c = _engine.Config;
            c.ProxyIpv6 = _ipv6.Checked;
            c.ProxyDns = _dns.Text.Trim();
            if (_engine.ApplyConnection(_endpoints.Text, _useProxy.Checked, _link.Text))
                Changed();
        }

        void OnEngineChanged()
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed) Changed(); })); }
            catch (InvalidOperationException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _engine.Changed -= OnEngineChanged;
            base.Dispose(disposing);
        }
    }
}
