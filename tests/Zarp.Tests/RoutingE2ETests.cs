using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Zarp.Core;

// Правила маршрутизации и выбор программ проверяются настоящим sing-box на localhost, без виртуального адаптера.
//
// Устройство стенда. Клиент - тот, что строит Zarp, но правила действуют на проверочный вход, а не на адаптер.
// Сервер Trojan на localhost перенаправляет ВСЁ, что дошло до него, на «ловушку прокси» (она отвечает PROXY).
// Прямые подключения клиента идут на «ловушку напрямую» (она отвечает DIRECT). По ответу видно, каким путём пошёл трафик.
static partial class Program
{
    sealed class Greeter : IDisposable
    {
        readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        readonly string _greeting;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Greeter(string greeting)
        {
            _greeting = greeting;
            _listener.Start();
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var client = _listener.AcceptTcpClient();
                        var bytes = Encoding.ASCII.GetBytes(_greeting);
                        try { client.GetStream().Write(bytes, 0, bytes.Length); Thread.Sleep(100); } catch { }
                        client.Close();
                    }
                }
                catch { } // остановлен
            }) { IsBackground = true }.Start();
        }

        public void Dispose() => _listener.Stop();
    }

    sealed class RoutingLab : IDisposable
    {
        public SingBox Box;
        public Greeter Direct = new Greeter("DIRECT"), ProxyTrap = new Greeter("PROXY");
        public string Link;
        public int ServerPort;
        Process _server;

        public void Start(SingBox box)
        {
            Box = box;
            string dir = Path.Combine(_data, "routing-lab");
            Directory.CreateDirectory(Path.Combine(dir, "server"));
            string serverExe = Path.Combine(dir, "server", "sing-box.exe");
            File.Copy(box.Exe, serverExe, true);
            string pem = SingBoxRun(box, "generate tls-keypair localhost").Output;
            File.WriteAllText(Path.Combine(dir, "key.pem"), Regex.Match(pem, "-----BEGIN PRIVATE KEY-----.*?-----END PRIVATE KEY-----", RegexOptions.Singleline).Value.Replace("\r", ""));
            File.WriteAllText(Path.Combine(dir, "cert.pem"), Regex.Match(pem, "-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----", RegexOptions.Singleline).Value.Replace("\r", ""));
            ServerPort = FreeTcpPort();
            var server = new Dictionary<string, object>
            {
                ["log"] = new Dictionary<string, object> { ["level"] = "info" },
                ["inbounds"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "trojan", ["listen"] = "127.0.0.1", ["listen_port"] = ServerPort,
                        ["users"] = new List<object> { new Dictionary<string, object> { ["name"] = "u", ["password"] = "test-password" } },
                        ["tls"] = new Dictionary<string, object> { ["enabled"] = true, ["certificate_path"] = Path.Combine(dir, "cert.pem"), ["key_path"] = Path.Combine(dir, "key.pem") },
                    },
                },
                ["outbounds"] = new List<object> { new Dictionary<string, object> { ["type"] = "direct", ["tag"] = "direct" } },
                ["route"] = new Dictionary<string, object>
                {
                    ["final"] = "direct",
                    // всё, что дошло до сервера, оказывается в ловушке: так видно, что трафик прошёл через сервер
                    ["rules"] = new List<object> { new Dictionary<string, object> { ["action"] = "route-options", ["override_address"] = "127.0.0.1", ["override_port"] = ProxyTrap.Port } },
                },
            };
            string config = Path.Combine(dir, "server.json");
            File.WriteAllText(config, new JavaScriptSerializer().Serialize(server));
            var log = new StringBuilder();
            _server = StartSingBoxServer(serverExe, config, dir, log);
            lock (log) Check(!_server.HasExited && log.ToString().Contains("sing-box started"), "The lab server must start:\n" + log);
            Link = "trojan://test-password@127.0.0.1:" + ServerPort + "?sni=localhost&allowInsecure=1";
        }

        /// <summary>Клиент Zarp с правилами и выбранными программами; возвращает порт проверочного входа.</summary>
        public int Client(RoutingPreset preset, string geoip = null, string geosite = null, List<string> apps = null, bool onProbe = true, bool ipv6 = true)
        {
            var profile = ProxyProfile.Parse(Link);
            var session = new ProxySession
            {
                Profile = profile, Address = "127.0.0.1", Port = FreeTcpPort(), User = "zarp", Password = "pw", Tun = false, RulesOnProbe = onProbe,
                Rules = RouteRules.Compile(preset, geoip, geosite, null), AppPaths = apps, Ipv6 = ipv6,
            };
            // Имена из SOCKS-запросов разворачивает действие resolve. В стенде настоящего DNS нет, поэтому DNS-сервер
            // заменён таблицей имён: проверяются правила, а не сеть. Остальная конфигурация - как в программе.
            var config = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(ProxyConfig.Build(session));
            var dns = (Dictionary<string, object>)config["dns"];
            var servers = ((System.Collections.ArrayList)dns["servers"]).Cast<object>().ToList();
            servers[1] = new Dictionary<string, object>
            {
                ["type"] = "hosts", ["tag"] = "remote",
                ["predefined"] = new Dictionary<string, object> { ["localhost"] = "127.0.0.1", ["www.example.org"] = "127.0.0.1" },
            };
            dns["servers"] = servers;
            var err = StartSingBoxClient(Box, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(config), session.Port);
            Check(err == null, "The lab client must start: " + err + Environment.NewLine + Box.ReadLog());
            return session.Port;
        }

        /// <summary>Открыть соединение через клиент и прочитать, чья ловушка ответила: DIRECT, PROXY или причину отказа.</summary>
        public string Reach(int socksPort, string host, int port)
        {
            try
            {
                using (var s = new Socks5Session(socksPort, "zarp", "pw"))
                {
                    int reply = IPAddress.TryParse(host, out var ip) ? s.Request(1, ip, port).Reply : s.RequestHost(1, host, port);
                    if (reply != 0) return "refused";
                    s.Tcp.ReceiveTimeout = 5000;
                    var buffer = new byte[16];
                    int n = s.Stream.Read(buffer, 0, buffer.Length);
                    return n > 0 ? Encoding.ASCII.GetString(buffer, 0, n) : "closed";
                }
            }
            catch (IOException) { return "failed"; }
            catch (SocketException) { return "failed"; }
        }

        public void Dispose()
        {
            Box?.Stop();
            try { if (_server != null && !_server.HasExited) _server.Kill(); } catch { }
            Direct.Dispose();
            ProxyTrap.Dispose();
        }
    }

    static void TestRoutingEndToEnd() => WithEmbeddedSingBox(box =>
    {
        using (var lab = new RoutingLab())
        {
            lab.Start(box);
            int d = lab.Direct.Port;
            string TestNet = "203.0.113.7"; // адрес документации: напрямую до него не достучаться, а через сервер ловушка отвечает при любом адресе

            // без правил локальная сеть остаётся локальной, всё остальное идёт через сервер
            int socks = lab.Client(Preset());
            Check(lab.Reach(socks, "127.0.0.1", d) == "DIRECT", "Without rules the local network goes direct");
            Check(lab.Reach(socks, TestNet, 80) == "PROXY", "Everything else goes through the server");

            // проверка соединения всегда идёт через сервер, даже если правила сказали бы иное
            socks = lab.Client(Preset(direct: "127.0.0.0/8", block: "127.0.0.1"), onProbe: false);
            Check(lab.Reach(socks, "127.0.0.1", d) == "PROXY", "The probe of a session without the adapter ignores the rules and uses the server");

            // «Напрямую» действует, «Прокси» сильнее «Напрямую», «Блок» сильнее обоих
            socks = lab.Client(Preset(direct: "127.0.0.0/8"));
            Check(lab.Reach(socks, "127.0.0.1", d) == "DIRECT", "Direct sends a range directly");
            socks = lab.Client(Preset(direct: "geoip:private", proxy: "127.0.0.1"));
            Check(lab.Reach(socks, "127.0.0.1", d) == "PROXY", "Proxy overrides Direct");
            socks = lab.Client(Preset(direct: "geoip:private", proxy: "127.0.0.1", block: "127.0.0.1"));
            string blocked = lab.Reach(socks, "127.0.0.1", d);
            Check(blocked != "DIRECT" && blocked != "PROXY", "Block overrides Proxy and Direct: " + blocked);
            socks = lab.Client(Preset(block: "127.0.0.1"));
            blocked = lab.Reach(socks, "127.0.0.1", d);
            Check(blocked != "DIRECT" && blocked != "PROXY", "Block overrides the built-in local network rule: " + blocked);
            socks = lab.Client(Preset(direct: "geoip:private", proxy: "::1"));
            Check(lab.Reach(socks, "127.0.0.1", d) == "DIRECT", "A rule for another address changes nothing");

            // все виды правил по домену: «Прокси» сильнее встроенного «Напрямую для локальных», поэтому PROXY - признак совпадения
            string site = WriteGeoFile("lab-geosite.dat", GeoCategory("lab", false, DomainRecord(3, "localhost", "mine"), DomainRecord(2, "unrelated.example")));
            string ip = WriteGeoFile("lab-geoip.dat", GeoCategory("loop", false, CidrRecord("127.0.0.0", 8)));
            foreach (var rule in new[] { "full:localhost", "domain:localhost", "localhost", "keyword:ocalh", "regexp:^local", "geosite:lab", "geosite:lab@mine" })
            {
                socks = lab.Client(Preset(direct: "geoip:private", proxy: rule), geoip: ip, geosite: site);
                string got = lab.Reach(socks, "localhost", d);
                Check(got == "PROXY", "A domain rule must match: " + rule + " -> " + got + Environment.NewLine + lab.Box.ReadLog());
            }
            foreach (var rule in new[] { "full:other.example", "domain:ocalhost", "keyword:remote", "regexp:^x", "geosite:lab@!mine" })
            {
                if (rule == "geosite:lab@!mine") continue; // у записей группы нет другого атрибута: такое правило и не соберётся
                socks = lab.Client(Preset(direct: "geoip:private", proxy: rule), geoip: ip, geosite: site);
                Check(lab.Reach(socks, "localhost", d) == "DIRECT", "A rule that does not match must change nothing: " + rule);
            }
            socks = lab.Client(Preset(direct: "geoip:private", proxy: "geoip:loop"), geoip: ip, geosite: site);
            Check(lab.Reach(socks, "127.0.0.1", d) == "PROXY", "A GeoIP category from the database routes by address");
            socks = lab.Client(Preset(proxy: "full:unrelated.example"));
            Check(lab.Reach(socks, "www.example.org", d) == "DIRECT", "Control: with rules present the domain is resolved and, being local, goes direct");
            socks = lab.Client(Preset(block: "domain:example.org"));
            blocked = lab.Reach(socks, "www.example.org", d);
            Check(blocked != "DIRECT" && blocked != "PROXY", "A blocked domain with subdomains is refused: " + blocked);

            // выбранные программы: остальные идут мимо сервера и правил; сравнение пути без учёта регистра
            string me = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var rulesForApps = Preset(direct: "geoip:private", proxy: "127.0.0.1");
            socks = lab.Client(rulesForApps, apps: new List<string> { @"C:\Some Other\app (x86)\other.exe" });
            Check(lab.Reach(socks, "127.0.0.1", d) == "DIRECT", "A program that is not selected bypasses the server and the rules");
            socks = lab.Client(rulesForApps, apps: new List<string> { me });
            Check(lab.Reach(socks, "127.0.0.1", d) == "PROXY", "A selected program follows the rules");
            socks = lab.Client(rulesForApps, apps: new List<string> { me.ToUpperInvariant() });
            Check(lab.Reach(socks, "127.0.0.1", d) == "PROXY", "Program paths are compared ignoring case");
            socks = lab.Client(rulesForApps, apps: new List<string> { @"C:\x\a.exe", me.ToLowerInvariant() });
            Check(lab.Reach(socks, "127.0.0.1", d) == "PROXY", "Any of several selected programs is enough");
        }
    });
}
