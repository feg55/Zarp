using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Zarp.Core;

// Собственный сервер вместо WARP: разбор ссылок, конфигурация sing-box, настоящие VLESS, Trojan и Hysteria2 на localhost.
// Ни виртуальный адаптер, ни настоящие WARP и WinDivert не запускаются.
static partial class Program
{
    const string TestUuid = "00000000-0000-4000-8000-000000000001";
    const string TestRealityKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; // 43 символа

    static Dictionary<string, object> JsonObject(string text) =>
        (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
    static Dictionary<string, object> Obj(object value) => (Dictionary<string, object>)value;
    static object[] Arr(object value) => (object[])value;

    static void TestProxyProfiles()
    {
        // пароль раскодируется, плюс остаётся плюсом, подпись после # игнорируется
        var trojan = ProxyProfile.Parse("trojan://p%40ss%2Bword@proxy.example:443?sni=front.example#My%20server");
        var o = trojan.BuildOutbound();
        Check((string)o["type"] == "trojan" && (string)o["tag"] == "proxy" && (string)o["password"] == "p@ss+word", "Trojan credentials are decoded");
        Check((string)o["server"] == "proxy.example" && (int)o["server_port"] == 443 && (string)Obj(o["tls"])["server_name"] == "front.example", "Trojan address and SNI");
        Check(trojan.Endpoint == "proxy.example:443" && trojan.Title == "Trojan" && trojan.Protocol == ProxyProtocol.Trojan, "Trojan summary");
        var strategy = trojan.ToStrategy();
        Check(strategy.IsProxy && !strategy.UsesZapret && strategy.Name == "Trojan: proxy.example:443" && strategy.ProtocolTitle == "Trojan" &&
              !(strategy.Id + strategy.Name).Contains("ss") && strategy.Id.StartsWith("proxy-"), "A server strategy never carries the password");
        Check(ProxyProfile.Parse("trojan://p%40ss%2Bword@proxy.example:443?sni=front.example").Id == trojan.Id &&
              ProxyProfile.Parse("trojan://p%40ss%2Bword@proxy.example:444?sni=front.example").Id != trojan.Id, "Any change of the link changes the identity");

        // сертификат проверяется, пока ссылка сама не отключит проверку; имя для TLS: sni, затем peer, затем адрес
        string Tls(string query, string field) => Convert.ToString(Obj(ProxyProfile.Parse("trojan://s@proxy.example:443" + query).BuildOutbound()["tls"])[field]);
        Check(Tls("", "insecure") == "False" && Tls("?insecure=0&allowInsecure=false", "insecure") == "False" && Tls("?allowInsecure=1", "insecure") == "True" &&
              Tls("?insecure=true", "insecure") == "True", "Certificates are verified unless the link switches the check off");
        Check(Tls("", "server_name") == "proxy.example" && Tls("?peer=peer.example", "server_name") == "peer.example" && Tls("?peer=peer.example&sni=sni.example", "server_name") == "sni.example",
            "The TLS name is sni, then peer, then the server address");

        // Hysteria2: порт по умолчанию 443, Salamander, схема hy2
        var hy2 = ProxyProfile.Parse("hy2://secret@proxy.example/?obfs=salamander&obfs-password=key&alpn=h3&insecure=1");
        o = hy2.BuildOutbound();
        Check(hy2.Endpoint == "proxy.example:443" && (string)o["type"] == "hysteria2" && (string)o["password"] == "secret", "Hysteria2 defaults");
        Check((string)Obj(o["obfs"])["type"] == "salamander" && (string)Obj(o["obfs"])["password"] == "key", "Salamander obfuscation");
        Check((bool)Obj(o["tls"])["insecure"] && ((List<string>)Obj(o["tls"])["alpn"]).Count == 1 && !Obj(o["tls"]).ContainsKey("utls"), "Hysteria2 TLS options (no uTLS)");
        Check(ProxyProfile.Parse("hysteria2://u:p@proxy.example:8443").Port == 8443, "The hysteria2 scheme and an explicit port");

        // VLESS: WebSocket, отпечаток TLS
        var vless = ProxyProfile.Parse("vless://" + TestUuid + "@proxy.example:443?security=tls&type=ws&path=%2Fsocket&host=front.example&fp=firefox&alpn=h2,http/1.1");
        o = vless.BuildOutbound();
        var transport = Obj(o["transport"]);
        Check((string)o["uuid"] == TestUuid && (string)o["flow"] == "" && (string)o["packet_encoding"] == "xudp", "VLESS basics");
        Check((string)transport["type"] == "ws" && (string)transport["path"] == "/socket" && (string)Obj(transport["headers"])["Host"] == "front.example", "WebSocket transport");
        Check((string)Obj(Obj(o["tls"])["utls"])["fingerprint"] == "firefox" && ((List<string>)Obj(o["tls"])["alpn"]).Count == 2, "uTLS fingerprint and ALPN list");
        Check((string)vless.BuildOutbound("203.0.113.5")["server"] == "203.0.113.5" &&
              (string)Obj(vless.BuildOutbound("203.0.113.5")["tls"])["server_name"] == "proxy.example", "A resolved address replaces the host but not the TLS name");

        // Reality + Vision, адрес IPv6
        var reality = ProxyProfile.Parse("vless://" + TestUuid + "@[2001:db8::1]:8443?security=reality&pbk=" + TestRealityKey + "&sid=0123abcd&flow=xtls-rprx-vision&sni=www.example.org");
        o = reality.BuildOutbound();
        var tls = Obj(o["tls"]);
        Check(reality.Endpoint == "[2001:db8::1]:8443" && (string)o["server"] == "2001:db8::1" && (string)o["flow"] == "xtls-rprx-vision", "Reality over IPv6 with Vision");
        Check((string)Obj(tls["reality"])["public_key"] == TestRealityKey && (string)Obj(tls["reality"])["short_id"] == "0123abcd" &&
              (string)Obj(tls["utls"])["fingerprint"] == "chrome" && (string)tls["server_name"] == "www.example.org", "Reality parameters and the default fingerprint");

        // gRPC и HTTPUpgrade
        o = ProxyProfile.Parse("trojan://secret@proxy.example:443?type=grpc&serviceName=tunnel").BuildOutbound();
        Check((string)Obj(o["transport"])["type"] == "grpc" && (string)Obj(o["transport"])["service_name"] == "tunnel", "gRPC service name");
        o = ProxyProfile.Parse("vless://" + TestUuid + "@proxy.example:443?security=tls&type=httpupgrade&path=/up&host=front.example").BuildOutbound();
        Check((string)Obj(o["transport"])["type"] == "httpupgrade" && (string)Obj(o["transport"])["host"] == "front.example", "HTTPUpgrade host");

        // неподдерживаемое отвергается, а не отбрасывается; в тексте ошибки нет секрета
        string[] bad =
        {
            "", "   ", "trojan://my-secret@host:99999", "trojan://my-secret@host:0", "trojan://my-secret@host", "trojan://my-secret@host:443?type=xhttp",
            "vless://bad@host:443", "https://my-secret@host:443", "trojan://@host:443", "trojan://my-secret@host:443/path", "trojan://my-secret@host:443?unknown=1",
            "vless://" + TestUuid + "@host:443?flow=xtls-rprx-vision", "vless://" + TestUuid + "@host:443?encryption=aes",
            "hy2://my-secret@host:443?type=ws", "trojan://my-secret@host:443?security=none", "hy2://my-secret@host:443?obfs=salamander",
            "hy2://my-secret@host:443?obfs=other&obfs-password=k", "trojan://my-secret@host:443?obfs=salamander&obfs-password=k",
            "vless://" + TestUuid + "@host:443?security=reality&pbk=short", "vless://" + TestUuid + "@host:443?security=reality&pbk=" + TestRealityKey + "&sid=abc",
            "trojan://my-secret@host:443?insecure=maybe", "trojan://my%ZZsecret@host:443", "trojan://my-secret@host:443?sni=a&sni=b",
            "trojan://my-secret@bad host:443", "trojan://my-secret@[::1:443", "trojan://my-secret@host:443?flow=xtls-rprx-vision",
            "vless://" + TestUuid + "@host:443?security=tls&type=tcp&flow=xtls-rprx-direct",
            "trojan://my-secret@[fe80::1%5]:443", "trojan://my%0Asecret@host:443", "trojan://my-secret@host:443?path=" + new string('a', 9000),
        };
        foreach (var link in bad)
        {
            var failure = Catch(() => ProxyProfile.Parse(link));
            Check(failure is FormatException && failure.Message == "proxy.invalid", "Must be rejected without details: " + link.Replace("my-secret", "***") + " -> " + failure?.Message);
            Check(!ProxyProfile.TryParse(link, out var none) && none == null, "TryParse must refuse: " + link.Replace("my-secret", "***"));
        }
        Check(ProxyProfile.TryParse("  trojan://secret@host:443  ", out _), "Surrounding spaces are ignored");
    }

    static ProxySession TestSession(ProxyProfile profile, bool tun) => new ProxySession
    {
        Profile = profile, Address = "203.0.113.5", Port = 18080, User = "zarp", Password = "pw", Tun = tun,
    };

    static void TestProxyConfig()
    {
        var profile = ProxyProfile.Parse("trojan://secret@proxy.example:443");
        Check(ProxyConfig.QuoteMeta(@"C:\a+b(1).exe") == @"C:\\a\+b\(1\)\.exe" && ProxyConfig.QuoteMeta("a b") == "a b", "Go-style regexp quoting (a space stays a space)");
        Check(ProxyConfig.AppPattern(@"C:\Tools\app.exe") == @"(?i)^C:\\Tools\\app\.exe$", "An app is matched by its whole path, ignoring case");

        // только проверка: проверочный вход и сервер, без адаптера
        var cfg = JsonObject(ProxyConfig.Build(TestSession(profile, false)));
        var inbounds = Arr(cfg["inbounds"]);
        var probe = Obj(inbounds.Single());
        Check((string)probe["type"] == "socks" && (string)probe["listen"] == "127.0.0.1" && (int)probe["listen_port"] == 18080, "The probe speaks SOCKS5 and listens on loopback only");
        Check((string)Obj(Arr(probe["users"]).Single())["username"] == "zarp" && (string)Obj(Arr(probe["users"]).Single())["password"] == "pw", "The probe is protected by credentials");
        var outbound = Obj(Arr(cfg["outbounds"])[0]);
        Check((string)outbound["tag"] == "proxy" && (string)outbound["server"] == "203.0.113.5" && (string)Obj(outbound["tls"])["server_name"] == "proxy.example", "The server is dialled by its resolved address");
        Check((string)Obj(Arr(cfg["outbounds"])[1])["type"] == "direct" && (string)Obj(cfg["route"])["final"] == "proxy" && (bool)Obj(cfg["route"])["auto_detect_interface"], "Unmatched traffic goes to the server");
        Check(Arr(Obj(cfg["route"])["rules"]).Length == 1, "A probe-only session has no rules but the probe one");

        // с адаптером, без правил
        cfg = JsonObject(ProxyConfig.Build(TestSession(profile, true)));
        var tun = Obj(Arr(cfg["inbounds"]).First());
        Check((string)tun["type"] == "tun" && (string)tun["interface_name"] == SingBox.TunName && (bool)tun["auto_route"] && (bool)tun["strict_route"] && (string)tun["stack"] == "gvisor", "The adapter is named Zarp and routes everything");
        var rules = Arr(Obj(cfg["route"])["rules"]).Select(Obj).ToList();
        Check(rules.Count == 3 && ((object[])rules[0]["inbound"])[0].Equals("probe-in") && (string)rules[0]["outbound"] == "proxy", "The probe goes first and always through the server");
        Check((string)rules[1]["action"] == "hijack-dns" && (int)rules[1]["port"] == 53, "DNS is answered by sing-box so that it travels through the server");
        Check((bool)rules[2]["ip_is_private"] && (string)rules[2]["outbound"] == "direct", "Without rules the local network stays local");
        Check(!(bool)Obj(cfg["dns"])["reverse_mapping"] && (string)Obj(cfg["dns"])["strategy"] == "prefer_ipv4", "No domain detection without rules");

        // правила пользователя, выбор программ, IPv6 выключен
        var session = TestSession(profile, true);
        session.Rules = new List<object>
        {
            new Dictionary<string, object> { ["type"] = "logical", ["mode"] = "or", ["rules"] = new List<object> { new Dictionary<string, object> { ["domain_keyword"] = new List<string> { "ads" } } }, ["action"] = "reject" },
            new Dictionary<string, object> { ["domain_suffix"] = new List<string> { "example.org" }, ["action"] = "route", ["outbound"] = "direct" },
        };
        session.AppPaths = new List<string> { @"C:\Program Files\App (x86)\app.exe", @"D:\Tools\a+b.exe" };
        session.Ipv6 = false;
        session.Dns = "bad, 9.9.9.9";
        cfg = JsonObject(ProxyConfig.Build(session));
        rules = Arr(Obj(cfg["route"])["rules"]).Select(Obj).ToList();
        var actions = rules.Select(r => r.ContainsKey("action") ? (string)r["action"] : "?").ToList();
        Check(actions.SequenceEqual(new[] { "route", "route", "reject", "sniff", "hijack-dns", "resolve", "reject", "route", "route" }),
            "Order: probe, app gate, IPv6 block, sniff, DNS, resolve, user rules, local network; got " + string.Join(",", actions));
        var gate = rules[1];
        var patterns = Arr(gate["process_path_regex"]).Cast<string>().ToList();
        Check((bool)gate["invert"] && (string)gate["outbound"] == "direct" && patterns.Count == 2 &&
              patterns[0] == @"(?i)^C:\\Program Files\\App \(x86\)\\app\.exe$" && patterns[1] == @"(?i)^D:\\Tools\\a\+b\.exe$", "Everything except the chosen programs goes direct: " + string.Join(" | ", patterns));
        Check((int)rules[2]["ip_version"] == 6, "IPv6 is rejected when it is off, so it cannot leak around the server");
        Check((bool)Obj(cfg["dns"])["reverse_mapping"] && (string)Obj(cfg["dns"])["strategy"] == "ipv4_only" && (string)Obj(Arr(Obj(cfg["dns"])["servers"])[1])["server"] == "9.9.9.9",
            "Domain rules need reverse mapping; the first valid DNS server is used");
        Check(Obj(Arr(Obj(cfg["dns"])["servers"])[1])["detour"].Equals("proxy"), "Remote DNS travels through the server");
        // собственные запросы Zarp при выборе программ идут через сервер: так проверяется, что адаптер пропускает трафик
        session = TestSession(profile, true);
        session.AppPaths = new List<string> { @"C:\Tools\app.exe" };
        session.OwnPath = @"C:\Zarp (x64)\Zarp.exe";
        rules = Arr(Obj(JsonObject(ProxyConfig.Build(session))["route"])["rules"]).Select(Obj).ToList();
        Check((string)rules[1]["outbound"] == "proxy" && !rules[1].ContainsKey("invert") && Arr(rules[1]["process_path_regex"]).Single().Equals(@"(?i)^C:\\Zarp \(x64\)\\Zarp\.exe$") &&
              (bool)rules[2]["invert"] && (string)rules[2]["outbound"] == "direct", "Zarp itself goes through the server and is checked before the program gate");
        session.AppPaths = null;
        Check(!ProxyConfig.Build(session).Contains("Zarp (x64)") && !ProxyConfig.Build(session).Contains("process_path_regex"), "Without a program choice no process rule is needed");
        session = TestSession(profile, true);
        // выбор программ без IPv6-запрета и правил не добавляет лишнего
        session.Rules = new List<object>();
        session.Ipv6 = true;
        session.AppPaths = null;
        actions = Arr(Obj(JsonObject(ProxyConfig.Build(session))["route"])["rules"]).Select(r => (string)Obj(r)["action"]).ToList();
        Check(actions.SequenceEqual(new[] { "route", "hijack-dns", "route" }), "No extra rules without options: " + string.Join(",", actions));
    }

    static void WithEmbeddedSingBox(Action<SingBox> test)
    {
        var embedded = typeof(SingBox).GetField("_embeddedVersion", PrivateStatic);
        embedded.SetValue(null, null); // остальные проверки идут с выключенным вшитым sing-box
        try
        {
            if (SingBox.EmbeddedVersion == null)
                throw new TestSkippedException("Zarp was built without embedded sing-box; run tools/fetch-singbox.ps1 before building.");
            var box = new SingBox(Path.Combine(_data, "singbox-run"));
            Check(box.ExtractEmbedded() && box.Installed && box.Version == SingBox.EmbeddedVersion, "The embedded sing-box must unpack");
            Console.WriteLine("Embedded sing-box: " + SingBox.EmbeddedVersion);
            test(box);
        }
        finally { embedded.SetValue(null, ""); }
    }

    // ------------------------------------------------------------------ запуск sing-box в тестах

    const int LoaderStatus = unchecked((int)0xC0000142); // STATUS_DLL_INIT_FAILED

    /// <summary>
    /// Повторяет запуск, только если Windows убила процесс до его кода (см. DiedInLoader в TestRunner.cs): такой запуск ничего не говорит
    /// о конфигурации. Запусков не больше LaunchAttempts, любой другой результат окончателен. Повтор остаётся в выводе случая
    /// под именем process, а CI превращает его в предупреждение; без имени (проверка самих правил) вывода и предупреждения нет.
    /// </summary>
    static T UntilLoaded<T>(string process, Func<T> launch, Func<T, bool> diedInLoader, int delayMs = 500)
    {
        var result = launch();
        for (int lost = 1; lost < LaunchAttempts && diedInLoader(result); lost++)
        {
            if (process != null)
                Console.WriteLine(LaunchRetryMarker + " the Windows loader killed " + process + " (0xC0000142, no output), launch " + lost + " of " + LaunchAttempts + ".");
            Thread.Sleep(delayMs * lost);
            result = launch();
        }
        return result;
    }

    static bool LoaderKilled(RunResult run) => !run.TimedOut && run.ExitCode == LoaderStatus && run.Output.Length == 0;

    // sing-box запускается через cmd, код выхода не виден: убитый загрузчиком процесс ничего не пишет в журнал, а настоящая ошибка пишет
    static bool SilentExit(Msg err) => err != null && err.Key == "err.singboxExitedSilent";

    static RunResult SingBoxRun(SingBox box, string args) =>
        UntilLoaded("sing-box", () => Sync(() => ProcessUtil.RunAsync(box.Exe, args, 30000)), LoaderKilled);

    static Msg StartSingBoxClient(SingBox box, string config, int port) =>
        UntilLoaded("the Zarp client", () => Sync(() => box.StartAsync(config, port)), SilentExit);

    /// <summary>Сервер для проверок: настоящий sing-box, запущенный как есть. Ждёт «sing-box started», вывод копится в log.</summary>
    static Process StartSingBoxServer(string exe, string config, string dir, StringBuilder log) =>
        UntilLoaded("the sing-box server", () =>
        {
            var server = new Process
            {
                StartInfo = new ProcessStartInfo(exe, "run -c \"" + config + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = dir,
                },
            };
            server.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
            server.OutputDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
            server.Start();
            server.BeginErrorReadLine();
            server.BeginOutputReadLine();
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 20000 && !server.HasExited) { lock (log) if (log.ToString().Contains("sing-box started")) break; Thread.Sleep(50); }
            return server;
        }, server =>
        {
            if (!server.HasExited) return false;
            server.WaitForExit(); // дочитать вывод до конца: пустой журнал значит, что процесс не успел ничего написать
            lock (log) return server.ExitCode == LoaderStatus && log.Length == 0;
        });

    /// <summary>Запуск, убитый загрузчиком, повторяется; любой другой результат, хороший или плохой, окончателен.</summary>
    static void TestSingBoxLaunchRetry()
    {
        RunResult Run(int code, string output = "", bool timedOut = false) => new RunResult { ExitCode = code, Output = output, TimedOut = timedOut };
        int launches = 0;
        RunResult Launch(params RunResult[] script)
        {
            launches = 0;
            return UntilLoaded(null, () => script[Math.Min(launches++, script.Length - 1)], LoaderKilled, 0);
        }

        var loader = Run(LoaderStatus);
        var good = Run(0, "configuration is valid");
        Check(Launch(loader, loader, good) == good && launches == 3, "A process killed by the loader must be started again until it runs");
        Check(Launch(loader) == loader && launches == LaunchAttempts, "A process that keeps dying in the loader must give up after " + LaunchAttempts + " launches");
        foreach (var final in new[]
        {
            good, Run(1, "invalid configuration"), Run(1), Run(LoaderStatus, "something was printed"), Run(LoaderStatus, "", true),
            Run(unchecked((int)0xC0000135)), Run(unchecked((int)0xC0000005)),
        })
            Check(Launch(final, good) == final && launches == 1, "Must not be started again: exit " + final.ExitCode + ", output '" + final.Output + "', timed out " + final.TimedOut);

        Check(SilentExit(new Msg("err.singboxExitedSilent")) && !SilentExit(new Msg("err.singboxExited", "FATAL start service: bad option")) &&
              !SilentExit(new Msg("err.singboxTimeout")) && !SilentExit(null), "Only a sing-box that exited without a word is started again");

        // настоящий процесс: код выхода приходит тем же числом, которое показывает загрузчик
        int real = 0;
        var cmd = UntilLoaded(null, () => Sync(() => ProcessUtil.RunAsync(ProcessUtil.SystemExe("cmd.exe"), "/d /c exit " + (real++ < 2 ? LoaderStatus : 0), 30000)), LoaderKilled, 0);
        Check(cmd.ExitCode == 0 && real == 3, "A real process that exits with the loader status must be started again, got exit " + cmd.ExitCode + " after " + real + " launches");
    }

    static void TestSingBoxConfigsAccepted() => WithEmbeddedSingBox(box =>
    {
        var links = new[]
        {
            "vless://" + TestUuid + "@proxy.example:443?security=tls&sni=front.example&fp=chrome&alpn=h2",
            "vless://" + TestUuid + "@proxy.example:443?security=tls&type=ws&path=%2Fws&host=front.example&allowInsecure=1",
            "vless://" + TestUuid + "@proxy.example:443?security=tls&type=grpc&serviceName=zz",
            "vless://" + TestUuid + "@proxy.example:443?security=tls&type=httpupgrade&path=%2Fup&host=front.example",
            "vless://" + TestUuid + "@[2001:db8::1]:8443?security=reality&pbk=" + TestRealityKey + "&sid=0123abcd&flow=xtls-rprx-vision&sni=www.example.org",
            "vless://" + TestUuid + "@proxy.example:8080",
            "trojan://secret@proxy.example:443",
            "trojan://secret@proxy.example:443?type=ws&path=/t&host=front.example&sni=front.example",
            "trojan://secret@proxy.example:443?type=grpc&serviceName=t",
            "trojan://secret@proxy.example:443?type=httpupgrade&host=front.example",
            "hy2://secret@proxy.example",
            "hysteria2://secret@proxy.example:8443/?sni=front.example&alpn=h3&obfs=salamander&obfs-password=key&insecure=1",
        };
        foreach (var link in links)
        foreach (bool tun in new[] { false, true })
        {
            var session = TestSession(ProxyProfile.Parse(link), tun);
            if (tun)
            {
                session.Rules = new List<object>
                {
                    new Dictionary<string, object> { ["ip_cidr"] = new List<string> { "10.0.0.0/8", "2001:db8::/32" }, ["action"] = "route", ["outbound"] = "direct" },
                    new Dictionary<string, object> { ["domain_regex"] = new List<string> { "^ads[0-9]+\\." }, ["action"] = "reject" },
                };
                session.AppPaths = new List<string> { @"C:\Program Files\Some App\app.exe" };
                session.Ipv6 = false;
            }
            string config = ProxyConfig.Build(session);
            var check = SingBoxRun(box, "check -c " + WriteTemp(config));
            Check(check.Ok, "sing-box must accept the configuration for " + link.Substring(0, link.IndexOf("://")) + " tun=" + tun + ":\n" + check.Output);
        }

        // и отвергает испорченную: проверка не пропускает всё подряд
        string good = ProxyConfig.Build(TestSession(ProxyProfile.Parse(links[0]), true));
        string broken = good.Replace("\"timestamp\":true", "\"timestamp\":true,\"bogus\":1");
        Check(broken != good && !SingBoxRun(box, "check -c " + WriteTemp(broken)).Ok, "A configuration with an unknown option must be rejected by sing-box check");
        var viaRuntime = Sync(() => box.CheckAsync(broken));
        Check(!viaRuntime.Ok && viaRuntime.Output.Contains("bogus"), "SingBox.CheckAsync reports the problem: " + viaRuntime.Output);
        // правила пользователя проверяет движок: неверное регулярное выражение RE2 или CIDR не дойдёт до подключения
        foreach (var rule in new[] { new Dictionary<string, object> { ["domain_regex"] = new List<string> { "[" }, ["action"] = "reject" },
                                     new Dictionary<string, object> { ["ip_cidr"] = new List<string> { "10.0.0.0/99" }, ["action"] = "reject" },
                                     new Dictionary<string, object> { ["domain_regex"] = new List<string> { "^(?=a)b" }, ["action"] = "reject" } })
        {
            var bad = TestSession(ProxyProfile.Parse(links[0]), true);
            bad.Rules = new List<object> { rule };
            var result = Sync(() => box.CheckAsync(ProxyConfig.Build(bad)));
            Check(!result.Ok && result.Output.Length > 0, "Invalid user rules must be rejected before connecting: " + rule.Keys.First());
        }
        Check(Sync(() => box.CheckAsync(ProxyConfig.Build(TestSession(ProxyProfile.Parse(links[0]), true)))).Ok, "SingBox.CheckAsync accepts a good configuration");
    });

    /// <summary>sing-box запускается, только если файл побайтно совпадает со вшитым: подмену exe ловит проверка хеша перед запуском.</summary>
    static void TestSingBoxTamper() => WithEmbeddedSingBox(box =>
    {
        using (var file = new FileStream(box.Exe, FileMode.Append)) file.WriteByte(0);
        var err = Sync(() => box.StartAsync("{}", FreeTcpPort()));
        Check(err != null && err.Key == "err.singboxChanged" && !box.Running, "A sing-box.exe that differs from the embedded one must not be started, got: " + err);
        Check(!File.Exists(Path.Combine(box.Dir, "config.json")), "A refused start leaves no configuration (it may hold the password) behind");
        Check(box.ExtractEmbedded() && box.Installed, "Unpacking puts the genuine file back");
        var version = SingBoxRun(box, "version");
        Check(version.Ok && version.Output.Contains(SingBox.EmbeddedVersion.TrimStart('v')), "The restored sing-box runs and reports the embedded version: " + version.Output);
    });

    static string WriteTemp(string text)
    {
        string path = Path.Combine(_data, "cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return "\"" + path + "\"";
    }

    // ------------------------------------------------------------------ настоящие серверы на localhost

    sealed class Socks5Session : IDisposable
    {
        public readonly TcpClient Tcp = new TcpClient();
        public NetworkStream Stream;

        public Socks5Session(int port, string user, string password)
        {
            Tcp.Connect(IPAddress.Loopback, port);
            Tcp.ReceiveTimeout = Tcp.SendTimeout = 15000;
            Stream = Tcp.GetStream();
            Stream.Write(new byte[] { 5, 1, 2 }, 0, 3);
            var method = Read(2);
            if (method[0] != 5 || method[1] != 2) throw new IOException("SOCKS5 method " + method[1]);
            var auth = new List<byte> { 1, (byte)user.Length };
            auth.AddRange(Encoding.ASCII.GetBytes(user));
            auth.Add((byte)password.Length);
            auth.AddRange(Encoding.ASCII.GetBytes(password));
            Stream.Write(auth.ToArray(), 0, auth.Count);
            var ok = Read(2);
            if (ok[1] != 0) throw new IOException("SOCKS5 authentication failed");
        }

        public byte[] Read(int count)
        {
            var buffer = new byte[count];
            int done = 0;
            while (done < count)
            {
                int n = Stream.Read(buffer, done, count - done);
                if (n <= 0) throw new IOException("SOCKS5 connection closed");
                done += n;
            }
            return buffer;
        }

        /// <summary>Команда (1 - CONNECT, 3 - UDP ASSOCIATE). Возвращает код ответа и адрес для UDP.</summary>
        public (int Reply, IPEndPoint Relay) Request(int command, IPAddress address, int port)
        {
            var request = new List<byte> { 5, (byte)command, 0, 1 };
            request.AddRange(address.GetAddressBytes());
            request.Add((byte)(port >> 8));
            request.Add((byte)port);
            Stream.Write(request.ToArray(), 0, request.Count);
            var head = Read(4);
            int length = head[3] == 1 ? 4 : head[3] == 4 ? 16 : Read(1)[0];
            var rest = Read(length + 2);
            IPEndPoint relay = null;
            if (head[3] == 1 || head[3] == 4)
                relay = new IPEndPoint(new IPAddress(rest.Take(length).ToArray()), (rest[length] << 8) | rest[length + 1]);
            return (head[1], relay);
        }

        /// <summary>То же с именем вместо адреса: имя разбирает сам sing-box. Возвращает код ответа.</summary>
        public int RequestHost(int command, string host, int port)
        {
            var name = Encoding.ASCII.GetBytes(host);
            var request = new List<byte> { 5, (byte)command, 0, 3, (byte)name.Length };
            request.AddRange(name);
            request.Add((byte)(port >> 8));
            request.Add((byte)port);
            Stream.Write(request.ToArray(), 0, request.Count);
            var head = Read(4);
            int length = head[3] == 1 ? 4 : head[3] == 4 ? 16 : Read(1)[0];
            Read(length + 2);
            return head[1];
        }

        public void Dispose() => Tcp.Close();
    }

    static string HttpThroughSocks(int socksPort, int targetPort)
    {
        using (var session = new Socks5Session(socksPort, "zarp", "pw"))
        {
            var reply = session.Request(1, IPAddress.Loopback, targetPort);
            if (reply.Reply != 0) throw new IOException("CONNECT failed with " + reply.Reply);
            var request = Encoding.ASCII.GetBytes("GET /ok HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            session.Stream.Write(request, 0, request.Length);
            var all = new MemoryStream();
            var buffer = new byte[8192];
            int n;
            while ((n = session.Stream.Read(buffer, 0, buffer.Length)) > 0) all.Write(buffer, 0, n);
            return Encoding.ASCII.GetString(all.ToArray());
        }
    }

    /// <summary>
    /// Один запрос GET, отправленный вручную как HTTP-прокси, без .NET. Возвращает первую строку ответа, "closed", если
    /// соединение закрыто без ответа, или "reset", если оно сброшено: sing-box так закрывает соединение после ошибки входа.
    /// </summary>
    static string RawProxyGet(int proxyPort, string user, string password, string url)
    {
        using (var tcp = new TcpClient())
        {
            tcp.Connect(IPAddress.Loopback, proxyPort);
            tcp.ReceiveTimeout = tcp.SendTimeout = 10000;
            var stream = tcp.GetStream();
            string auth = user == null ? "" : "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(user + ":" + password)) + "\r\n";
            var uri = new Uri(url);
            var request = Encoding.ASCII.GetBytes("GET " + url + "?x=1 HTTP/1.1\r\nHost: " + uri.Authority + "\r\n" + auth + "Connection: close\r\n\r\n");
            try
            {
                stream.Write(request, 0, request.Length);
                var head = new StringBuilder();
                int b;
                while ((b = stream.ReadByte()) >= 0 && b != '\n') head.Append((char)b);
                return head.Length == 0 ? "closed" : head.ToString().TrimEnd('\r');
            }
            catch (IOException) { return "reset"; }
        }
    }

    /// <summary>Клиент SOCKS5 предлагает только вход без пароля. IOException - вход отказал (ответ 05 FF) или оборвал соединение.</summary>
    static void SocksWithoutPassword(int port)
    {
        using (var tcp = new TcpClient())
        {
            tcp.Connect(IPAddress.Loopback, port);
            tcp.ReceiveTimeout = tcp.SendTimeout = 10000;
            var stream = tcp.GetStream();
            stream.Write(new byte[] { 5, 1, 0 }, 0, 3);
            var reply = new byte[2];
            for (int done = 0; done < 2;)
            {
                int n = stream.Read(reply, done, 2 - done);
                if (n <= 0) throw new IOException("connection closed");
                done += n;
            }
            if (reply[0] == 5 && reply[1] == 0) return; // вход согласился обойтись без пароля
            throw new IOException("refused, method " + reply[1]);
        }
    }

    static string UdpThroughSocks(int socksPort)
    {
        using (var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var echoer = new Thread(() =>
            {
                try
                {
                    var peer = new IPEndPoint(IPAddress.Any, 0);
                    var data = echo.Receive(ref peer);
                    echo.Send(data, data.Length, peer);
                }
                catch { }
            }) { IsBackground = true };
            echoer.Start();
            using (var session = new Socks5Session(socksPort, "zarp", "pw"))
            {
                var reply = session.Request(3, IPAddress.Any, 0);
                if (reply.Reply != 0) throw new IOException("UDP ASSOCIATE failed with " + reply.Reply);
                var relay = reply.Relay.Address.Equals(IPAddress.Any) ? new IPEndPoint(IPAddress.Loopback, reply.Relay.Port) : reply.Relay;
                int port = ((IPEndPoint)echo.Client.LocalEndPoint).Port;
                var packet = new List<byte> { 0, 0, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port };
                packet.AddRange(Encoding.ASCII.GetBytes("zarp-udp"));
                using (var udp = new UdpClient())
                {
                    udp.Client.ReceiveTimeout = 10000;
                    udp.Send(packet.ToArray(), packet.Count, relay);
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var data = udp.Receive(ref from);
                    return Encoding.ASCII.GetString(data, data.Length - 8, 8);
                }
            }
        }
    }

    static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    sealed class LoopbackServer : IDisposable
    {
        public string Link;
        public Process Process;
        public readonly StringBuilder Log = new StringBuilder();

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(); } catch { }
            Process.Dispose();
        }
    }

    /// <summary>
    /// Настоящий сервер sing-box выбранного протокола на localhost. Link - ссылка, по которой к нему подключается клиент Zarp.
    /// hosts - имена, которые сервер сам разрешает в адреса: так до localhost можно дойти по имени, а .NET Framework никогда не
    /// отправляет через прокси адреса localhost и 127.0.0.1.
    /// </summary>
    static LoopbackServer StartLoopbackServer(SingBox box, string variant, Dictionary<string, string> hosts = null)
    {
        string protocol = variant.Split('-')[0];
        string suffix = variant.Contains("-") ? variant.Substring(variant.IndexOf('-') + 1) : "";
        string dir = Path.Combine(_data, "loopback-" + variant);
        Directory.CreateDirectory(dir);
        // серверу нужен свой экземпляр exe: SingBox.Stop() останавливает все процессы по пути, в том числе его
        string serverExe = Path.Combine(dir, "server", "sing-box.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(serverExe));
        File.Copy(box.Exe, serverExe, true);

        var pem = SingBoxRun(box, "generate tls-keypair localhost").Output;
        string key = System.Text.RegularExpressions.Regex.Match(pem, "-----BEGIN PRIVATE KEY-----.*?-----END PRIVATE KEY-----", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        string cert = System.Text.RegularExpressions.Regex.Match(pem, "-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        Check(key.Length > 0 && cert.Length > 0, "sing-box must generate a test certificate");
        File.WriteAllText(Path.Combine(dir, "cert.pem"), cert.Replace("\r", ""));
        File.WriteAllText(Path.Combine(dir, "key.pem"), key.Replace("\r", ""));

        int serverPort = FreeTcpPort();
        var user = protocol == "vless"
            ? new Dictionary<string, object> { ["name"] = "u", ["uuid"] = TestUuid }
            : new Dictionary<string, object> { ["name"] = "u", ["password"] = "test-password" };
        var inbound = new Dictionary<string, object>
        {
            ["type"] = protocol, ["listen"] = "127.0.0.1", ["listen_port"] = serverPort, ["users"] = new List<object> { user },
            ["tls"] = new Dictionary<string, object>
            {
                ["enabled"] = true, ["certificate_path"] = Path.Combine(dir, "cert.pem"), ["key_path"] = Path.Combine(dir, "key.pem"),
            },
        };
        string link;
        switch (variant)
        {
            case "vless": link = "vless://" + TestUuid + "@127.0.0.1:" + serverPort + "?security=tls&sni=localhost&allowInsecure=1"; break;
            case "trojan": link = "trojan://test-password@127.0.0.1:" + serverPort + "?sni=localhost&allowInsecure=1"; break;
            case "hysteria2":
                inbound["obfs"] = new Dictionary<string, object> { ["type"] = "salamander", ["password"] = "obfs-key" };
                link = "hy2://test-password@127.0.0.1:" + serverPort + "/?sni=localhost&insecure=1&obfs=salamander&obfs-password=obfs-key";
                break;
            case "vless-ws":
                inbound["transport"] = new Dictionary<string, object> { ["type"] = "ws", ["path"] = "/ws" };
                link = "vless://" + TestUuid + "@127.0.0.1:" + serverPort + "?security=tls&sni=localhost&allowInsecure=1&type=ws&path=%2Fws&host=localhost";
                break;
            case "trojan-grpc":
                inbound["transport"] = new Dictionary<string, object> { ["type"] = "grpc", ["service_name"] = "tunnel" };
                link = "trojan://test-password@127.0.0.1:" + serverPort + "?sni=localhost&allowInsecure=1&type=grpc&serviceName=tunnel";
                break;
            case "vless-httpupgrade":
                inbound["transport"] = new Dictionary<string, object> { ["type"] = "httpupgrade", ["path"] = "/up" };
                link = "vless://" + TestUuid + "@127.0.0.1:" + serverPort + "?security=tls&sni=localhost&allowInsecure=1&type=httpupgrade&path=%2Fup&host=localhost";
                break;
            default: throw new ArgumentException(variant);
        }
        var server = new Dictionary<string, object>
        {
            ["log"] = new Dictionary<string, object> { ["level"] = "info" },
            ["inbounds"] = new List<object> { inbound },
            ["outbounds"] = new List<object> { new Dictionary<string, object> { ["type"] = "direct", ["tag"] = "direct" } },
            ["route"] = new Dictionary<string, object> { ["final"] = "direct" },
        };
        if (hosts != null)
        {
            server["dns"] = new Dictionary<string, object>
            {
                ["servers"] = new List<object> { new Dictionary<string, object> { ["type"] = "hosts", ["tag"] = "names", ["predefined"] = hosts } },
                ["final"] = "names",
            };
            ((Dictionary<string, object>)server["route"])["default_domain_resolver"] = "names";
        }
        string serverConfig = Path.Combine(dir, "server.json");
        File.WriteAllText(serverConfig, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(server));
        var serverCheck = SingBoxRun(box, "check -c \"" + serverConfig + "\"");
        Check(serverCheck.Ok, "The test server configuration must be valid:\n" + serverCheck.Output);

        var result = new LoopbackServer { Link = link };
        result.Process = StartSingBoxServer(serverExe, serverConfig, dir, result.Log);
        lock (result.Log) Check(!result.Process.HasExited && result.Log.ToString().Contains("sing-box started"), "The test server must start:\n" + result.Log);
        return result;
    }

    /// <summary>
    /// Поднимает на localhost настоящий сервер sing-box выбранного протокола и подключается к нему клиентом Zarp
    /// (та же сборка конфигурации, что в программе). Через клиента проходят TCP и UDP.
    /// </summary>
    static void TestSingBoxLoopback(string variant) => WithEmbeddedSingBox(box =>
    {
        using (var web = new FakeServer(new byte[64 * 1024]))
        using (var server = StartLoopbackServer(box, variant))
        {
            try
            {
                var profile = ProxyProfile.Parse(server.Link);
                var session = TestSession(profile, false);
                session.Address = "127.0.0.1";
                session.Port = FreeTcpPort();
                var err = StartSingBoxClient(box, ProxyConfig.Build(session), session.Port);
                Check(err == null, "The Zarp client must start: " + err + "\n" + box.ReadLog());
                Check(box.Running && !File.Exists(Path.Combine(box.Dir, "config.json")), "The configuration with the password is deleted once the client runs");

                int webPort = int.Parse(web.Url("/").Split(':')[2].TrimEnd('/'));
                string response = HttpThroughSocks(session.Port, webPort);
                Check(response.StartsWith("HTTP/1.1 200") && response.Length > 64 * 1024, variant + ": TCP traffic must pass through the server, got " + response.Length + " bytes");
                Check(UdpThroughSocks(session.Port) == "zarp-udp", variant + ": UDP traffic must pass through the server");
            }
            finally { box.Stop(); }
        }
        Check(!box.Running, "Stop must end the client");
    });

    /// <summary>
    /// Проверка соединения (ProxyRuntime.MeasureAsync) через проверочный вход настоящего sing-box (SOCKS5 с паролем): запросы
    /// идут через сервер до страницы на localhost, интернет не нужен. До страницы идут по имени, которое разрешает сервер, как
    /// в программе; сам разбор ответов проверяет TestProbeClient.
    /// </summary>
    static void TestProxyMeasure() => WithEmbeddedSingBox(box =>
    {
        var runtime = new ProxyRuntime(box);
        string Named(string url) => url.Replace("127.0.0.1", "trace.zarp.test");
        using (var web = new FakeServer(new byte[16]))
        using (var server = StartLoopbackServer(box, "trojan", new Dictionary<string, string> { ["trace.zarp.test"] = "127.0.0.1" }))
        {
            try
            {
                var session = runtime.NewSession(ProxyProfile.Parse(server.Link));
                session.Address = "127.0.0.1";
                var err = Sync(() => runtime.StartAsync(session, CancellationToken.None));
                Check(err == null, "The probe session must start: " + err + "\n" + box.ReadLog());
                Check(session.User == "zarp" && session.Password.Length >= 32, "The probe port gets random credentials");

                SetPrivate(runtime, "TraceUrl", Named(web.Url("/trace")));
                var good = Sync(() => runtime.MeasureAsync(session, 2, 10000, CancellationToken.None));
                // на localhost целый запрос может занять меньше миллисекунды, поэтому задержка допускает 0
                Check(good.Ok && good.FirstMs >= 1 && good.PingMs >= 0 && good.Failed == 0 && web.Requests == 3,
                    "One warm-up and two samples must reach the page through the server: ok=" + good.Ok + ", first=" + good.FirstMs + ", ping=" + good.PingMs +
                    ", requests=" + web.Requests + ", failed=" + good.Failed + ", last error=" + good.Error);

                SetPrivate(runtime, "TraceUrl", Named(web.Url("/portal")));
                var portal = Sync(() => runtime.MeasureAsync(session, 1, 10000, CancellationToken.None));
                Check(!portal.Ok && portal.Error == "trace", "A 200 page without the ip field (a provider's stub) is not a successful check, got " + portal.Error);

                SetPrivate(runtime, "TraceUrl", Named(web.Url("/missing")));
                var missing = Sync(() => runtime.MeasureAsync(session, 1, 10000, CancellationToken.None));
                Check(!missing.Ok && !string.IsNullOrEmpty(missing.Error), "An error answer is a failed check");

                // проверочный вход закрыт для тех, кто не знает случайный пароль; HTTP-прокси на нём нет. До страницы запрос не доходит
                int before = web.Requests;
                var wrong = new ProxySession { Profile = session.Profile, Port = session.Port, User = session.User, Password = "wrong" };
                var refused = Sync(() => runtime.MeasureAsync(wrong, 1, 5000, CancellationToken.None));
                Check(!refused.Ok && refused.Failed == 2, "A wrong password fails every request of the check, got ok=" + refused.Ok + ", failed=" + refused.Failed + ", " + refused.Error);
                Check(Catch(() => new Socks5Session(session.Port, session.User, "wrong")) is IOException, "The probe port must refuse a wrong password over SOCKS5");
                Check(Catch(() => SocksWithoutPassword(session.Port)) is IOException, "The probe port must refuse a client that offers no password");
                string viaHttp = RawProxyGet(session.Port, session.User, session.Password, Named(web.Url("/trace")));
                Check(!viaHttp.StartsWith("HTTP/") && web.Requests == before, "The probe port does not speak HTTP proxy, got '" + viaHttp + "'; page requests " + before + " -> " + web.Requests);

                // https: после рукопожатия SOCKS5 клиент начинает TLS, поэтому до цели доходит начало рукопожатия
                var trap = new TcpListener(IPAddress.Loopback, 0);
                trap.Start();
                try
                {
                    var hello = new byte[3];
                    int got = 0;
                    var accepted = Task.Run(() =>
                    {
                        using (var client = trap.AcceptTcpClient())
                        {
                            client.ReceiveTimeout = 10000;
                            got = client.GetStream().Read(hello, 0, 3);
                        }
                    });
                    SetPrivate(runtime, "TraceUrl", Named("https://127.0.0.1:" + ((IPEndPoint)trap.LocalEndpoint).Port + "/cdn-cgi/trace"));
                    // исход известен заранее (цель не говорит по TLS), а проверка может ждать конца срока: хватит короткого
                    var tls = Sync(() => runtime.MeasureAsync(session, 0, 2500, CancellationToken.None));
                    Check(!tls.Ok, "A target that does not speak TLS is a failed check");
                    Check(accepted.Wait(10000) && got == 3 && hello[0] == 0x16 && hello[1] == 0x03,
                        "https must go through the SOCKS5 login, so the target receives a TLS ClientHello");
                }
                finally { trap.Stop(); }

                // проверка через адаптер идёт без прокси и считает успехом только ответ 200
                SetPrivate(runtime, "TraceUrl", web.Url("/trace"));
                Check(Sync(() => runtime.CheckTunnelAsync(5000, CancellationToken.None)), "The tunnel check accepts a reachable page");
                SetPrivate(runtime, "TraceUrl", web.Url("/missing"));
                Check(!Sync(() => runtime.CheckTunnelAsync(5000, CancellationToken.None)), "The tunnel check rejects an error answer");
            }
            finally { box.Stop(); }
        }
    });
}
