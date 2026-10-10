using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zarp.Core;

// Движок в режиме собственного сервера. sing-box подменён: ни процессов, ни сети, ни адаптера.
static partial class Program
{
    sealed class FakeProxy : ProxyRuntime
    {
        public readonly List<ProxySession> Started = new List<ProxySession>();
        public Func<ProxySession, Msg> StartResult = s => null;
        public Func<ProxySession, ProxyMeasure> Measure = s => new ProxyMeasure { Ok = true, FirstMs = 120, PingMs = 40 };
        public ProxySession AdoptResult;
        public bool IsRunning, Adapter = true, TunnelOk = true;
        public int Stops;

        public FakeProxy(string dir) : base(new SingBox(dir)) { }
        public override bool Running => IsRunning;
        public override void Stop() { Stops++; IsRunning = false; }
        public override bool Prepare() => true;
        public override ProxySession NewSession(ProxyProfile profile) => new ProxySession { Profile = profile, Port = 18080, User = "zarp", Password = "pw" };
        public override Task<string> ResolveAsync(string host, CancellationToken ct) => Task.FromResult("203.0.113.9");
        public override Task<Msg> StartAsync(ProxySession session, CancellationToken ct)
        {
            Started.Add(session);
            var err = StartResult(session);
            IsRunning = err == null;
            return Task.FromResult(err);
        }
        public override Task<ProxyMeasure> MeasureAsync(ProxySession session, int samples, int timeoutMs, CancellationToken ct) => Task.FromResult(Measure(session));
        public override Task<bool> WaitAdapterAsync(CancellationToken ct) => Task.FromResult(Adapter);
        public override Task<bool> CheckTunnelAsync(int timeoutMs, CancellationToken ct) => Task.FromResult(TunnelOk);
        public override string LastError() => null;
        public override ProxySession Adopt(ProxyProfile profile) => AdoptResult;
    }

    const string TestLink = "trojan://secret@proxy.example:443?sni=front.example";

    static Engine ProxyEngine(out FakeProxy fake, string link = TestLink)
    {
        var engine = NewEngine();
        fake = new FakeProxy(Path.Combine(engine.DataDir, "fake-singbox"));
        SetPrivate(engine, "Proxy", fake);
        SetPrivate(engine, "FindForeignVpns", (Func<List<string>>)(() => new List<string>()));
        engine.Config.UseProxy = true;
        engine.Config.ProxyUri = link;
        engine.ReloadStrategies();
        return engine;
    }

    static async Task TestProxyModeEngineAsync()
    {
        // WARP в этом движке не установлен: любое обращение к warp-cli упало бы с ошибкой
        var engine = ProxyEngine(out var fake);
        var profile = ProxyProfile.Parse(TestLink);
        Check(engine.ProxyMode && engine.Strategies.Count == 1 && engine.Strategies[0].IsProxy && engine.Strategies[0].Id == profile.Id &&
              engine.Candidates.Count == 1, "A server replaces the whole strategy list");
        engine.Config.StrategyCountry = "cn";
        Check(engine.Candidates.Count == 1, "The country filter does not touch a custom server");
        engine.Config.StrategyCountry = "all";

        // с нуля: проверка, перепроверка, потом подключение (сначала без адаптера, потом с ним)
        await engine.ConnectAsync();
        Check(engine.State == EngineState.Connected && engine.Detail == L.T("detail.strategy", engine.Strategies[0].Name), "Connected: " + engine.State + " " + engine.Detail);
        Check(fake.Started.Select(s => s.Tun).SequenceEqual(new[] { false, false, false, true }),
            "Test, recheck and the probe run without the adapter; the real connection has it: " + string.Join(",", fake.Started.Select(s => s.Tun)));
        Check(fake.Started.All(s => s.Address == "203.0.113.9" && s.Ipv6 && s.Dns == "1.1.1.1" && s.Profile.Id == profile.Id), "Sessions use the resolved address and the settings");
        Check(fake.IsRunning && engine.Config.SelectedStrategyId == profile.Id, "The tunnel runs and the server is remembered");
        var result = engine.Config.Results[profile.Id];
        Check(result.Ok && result.Confirmed && result.ConnectMs == 120 && result.PingMs == 40 && !result.Independent && result.Endpoint == "proxy.example:443",
            "The result keeps the time of the first answer and the ping, and is honestly marked as not independent");
        Check(AppConfig.Load(Path.Combine(engine.DataDir, "zarp.json")).Results.ContainsKey(profile.Id), "The result is saved");

        // наблюдение: жив и отвечает
        await engine.MonitorAsync();
        Check(engine.State == EngineState.Connected, "A running tunnel stays connected under observation");
        fake.Measure = s => new ProxyMeasure { Error = "timeout" };
        await engine.MonitorAsync();
        Check(engine.State == EngineState.Unknown && engine.Detail == L.T("err.proxyNoTraffic"), "A tunnel that stopped carrying traffic is reported: " + engine.Detail);
        fake.Measure = s => new ProxyMeasure { Ok = true, FirstMs = 100, PingMs = 30 };
        await engine.MonitorAsync();
        Check(engine.State == EngineState.Connected, "The state recovers when traffic returns");
        fake.IsRunning = false;
        await engine.MonitorAsync();
        Check(engine.State == EngineState.Unknown && engine.Detail == L.T("detail.singboxFailed"), "A vanished sing-box is reported: " + engine.State + " " + engine.Detail);
        await engine.MonitorAsync();
        Check(engine.State == EngineState.Idle && engine.Detail == L.T("detail.disconnected"), "The next observation, with nothing running, says disconnected: " + engine.Detail);
        await engine.MonitorAsync();
        Check(engine.State == EngineState.Idle, "An idle state is left alone");

        // отключение останавливает sing-box; повторное подключение идёт без поиска
        fake.IsRunning = true;
        await engine.DisconnectAsync();
        Check(!fake.IsRunning && engine.State == EngineState.Idle, "Disconnect stops sing-box");
        int before = fake.Started.Count;
        await engine.ConnectAsync();
        Check(engine.State == EngineState.Connected && fake.Started.Count - before == 2, "Reconnecting with the saved server needs only the probe and the tunnel");

        // правила и программы доходят до сессии с адаптером
        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        engine.Config.PerAppProxy = true;
        engine.Config.ProxyApps = new List<string> { exe, Path.Combine(_data, "gone.exe") };
        engine.Config.ProxyIpv6 = false;
        engine.Config.ProxyDns = "9.9.9.9";
        SetPrivate(engine, "CompileRules", (Func<AppConfig, List<object>>)(config =>
            new List<object> { new Dictionary<string, object> { ["domain_suffix"] = new List<string> { "example.org" }, ["action"] = "reject" } }));
        fake.Started.Clear();
        await engine.ConnectAsync();
        var tun = fake.Started.Last();
        Check(tun.Tun && tun.AppPaths != null && tun.AppPaths.SequenceEqual(new[] { exe }) && tun.Rules.Count == 1 && !tun.Ipv6 && tun.Dns == "9.9.9.9",
            "Only existing programs, the rules and the options reach the tunnel session");
        Check(fake.Started.Where(s => !s.Tun).All(s => s.AppPaths == null && s.Rules.Count == 0), "A server check ignores rules and the program list");

        // правила не собрались (нет базы, неизвестная категория): подключения нет, объяснение остаётся
        SetPrivate(engine, "CompileRules", (Func<AppConfig, List<object>>)(config => throw new InvalidOperationException("geoip:xx: category not found")));
        await engine.DisconnectAsync();
        await engine.ConnectAsync();
        Check(engine.State == EngineState.Idle && !fake.IsRunning && engine.Detail.Contains("category not found"), "Broken routing rules stop the connection with a reason: " + engine.State + " " + engine.Detail);
        SetPrivate(engine, "CompileRules", (Func<AppConfig, List<object>>)(config => new List<object>()));

        // пустой выбор программ не превращается в «все программы»
        await engine.DisconnectAsync();
        engine.Config.ProxyApps = new List<string> { Path.Combine(_data, "gone.exe") };
        fake.Started.Clear();
        await engine.ConnectAsync();
        Check(fake.Started.Count == 0 && engine.State == EngineState.Idle && engine.Detail == L.T("apps.empty"), "Missing programs refuse to connect: " + engine.Detail);
        engine.Config.PerAppProxy = false;
    }

    static async Task TestProxyModeFailuresAsync()
    {
        // сервер не отвечает: поиск не находит ничего, причина записана
        var engine = ProxyEngine(out var fake);
        fake.Measure = s => new ProxyMeasure { Error = "timeout" };
        await engine.ConnectAsync();
        string id = engine.Strategies[0].Id;
        Check(engine.State == EngineState.Idle && engine.Detail == L.T("detail.notFound") && !fake.IsRunning, "A dead server is not connected: " + engine.Detail);
        var failed = engine.Config.Results[id];
        Check(!failed.Ok && failed.ErrorText == L.T("err.proxyNoTraffic") && fake.Started.All(s => !s.Tun), "The failure is stored; the adapter is never started for a dead server");

        // sing-box не запустился
        engine = ProxyEngine(out fake);
        fake.StartResult = s => new Msg("err.singboxExited", "boom");
        await engine.ConnectAsync();
        Check(engine.Config.Results[engine.Strategies[0].Id].ErrorText == L.T("err.singboxExited", "boom"), "A start failure is reported as its own reason");

        // сохранённый сервер перестал отвечать: сразу отчёт, без повторного перебора тех же проверок
        engine = ProxyEngine(out fake);
        await engine.ConnectAsync();
        Check(engine.State == EngineState.Connected, "Setup: connected");
        await engine.DisconnectAsync();
        fake.Measure = s => new ProxyMeasure { Error = "timeout" };
        fake.Started.Clear();
        await engine.ConnectAsync();
        Check(engine.State == EngineState.Idle && engine.Detail == L.T("detail.connectFailed") && fake.Started.Count == 1 && !engine.Config.Results[engine.Strategies[0].Id].Ok,
            "A saved server that fails is reported at once and the failure is remembered: " + engine.Detail + " / " + fake.Started.Count);

        // адаптер не поднялся: sing-box остановлен
        engine = ProxyEngine(out fake);
        fake.Adapter = false;
        await engine.ConnectAsync();
        Check(engine.State == EngineState.Idle && !fake.IsRunning, "Without the adapter nothing is left running");

        // адаптер поднялся, но трафик через него не идёт: sing-box останавливается, сеть возвращается, результат помечается
        engine = ProxyEngine(out fake);
        fake.TunnelOk = false;
        await engine.ConnectAsync();
        Check(engine.State == EngineState.Idle && !fake.IsRunning && engine.Detail == L.T("detail.foundButFailed") && fake.Started.Last().Tun,
            "A dead adapter is removed instead of leaving the computer without a network: " + engine.State + " " + engine.Detail);

        // ссылка неверна: ничего не запускается
        engine = ProxyEngine(out fake, "vless://bad");
        Check(engine.Strategies.Count == 0, "An invalid link gives no strategies");
        await engine.ConnectAsync();
        Check(fake.Started.Count == 0 && engine.State == EngineState.Idle && engine.Detail == L.T("detail.proxyInvalid"), "An invalid link stops before sing-box: " + engine.Detail);

        // сторонний VPN: так же, как в режиме WARP
        engine = ProxyEngine(out fake);
        SetPrivate(engine, "FindForeignVpns", (Func<List<string>>)(() => new List<string> { "happ-tun" }));
        await engine.SearchAsync(false);
        Check(fake.Started.Count == 0 && engine.Detail == L.T("detail.vpnOff"), "A search through another VPN is refused: " + engine.Detail);

        // подхват подключения после перезапуска Zarp
        engine = ProxyEngine(out fake);
        fake.IsRunning = true;
        fake.AdoptResult = new ProxySession { Profile = ProxyProfile.Parse(TestLink), Port = 18080, User = "zarp", Password = "pw", Tun = true };
        await engine.RefreshStateAsync();
        Check(engine.State == EngineState.Connected, "A running tunnel from the previous run is picked up: " + engine.State);
        fake.IsRunning = false;
        engine = ProxyEngine(out fake);
        await engine.RefreshStateAsync();
        Check(engine.State == EngineState.Idle && fake.Started.Count == 0, "Nothing running means disconnected");

        // sing-box, который не удаётся подхватить (потеряны сведения или сменился сервер), не остаётся работать втайне
        engine = ProxyEngine(out fake);
        fake.IsRunning = true;
        fake.AdoptResult = null;
        await engine.RefreshStateAsync();
        Check(engine.State == EngineState.Idle && !fake.IsRunning && fake.Stops > 0, "A leftover sing-box that cannot be taken over is stopped");

        // режим собственного сервера не нужен для WARP: без него warp-cli не вызывается
        Check(Catch(() => engine.Warp.Cli("status").GetAwaiter().GetResult()) is InvalidOperationException, "Setup: this engine has no WARP");
    }

    static void TestProxyModeWindows()
    {
        var engine = ProxyEngine(out var fake);
        using (var main = NewMain(engine))
        {
            var sub = GetPrivate<Label>(main, "_sub");
            var hint = GetPrivate<Label>(main, "_hint");
            Check(sub.Text == L.T("proxy.subtitle") && hint.Text == L.T("hint.proxyFirstRun"), "The window speaks of the server, not WARP: " + sub.Text + " / " + hint.Text);
            engine.Config.UseProxy = false;
            main.GetType().GetMethod("UpdateUi", PrivateInstance).Invoke(main, null);
            Check(sub.Text == L.T("main.subtitle") && hint.Text == L.T("hint.installWarp"), "Back to WARP texts");
        }
        engine.Config.UseProxy = true;
        engine.ReloadStrategies();
        using (var settings = NewForm("SettingsForm", engine))
        {
            PositionOffscreen(settings);
            settings.Show();
            Application.DoEvents();
            var list = GetPrivate<ListView>(settings, "_list");
            Check(list.Items.Count == 1 && list.Items[0].SubItems[2].Text == "Trojan" && list.Items[0].SubItems[1].Text == "Trojan: proxy.example:443",
                "The settings list shows the server");
            Check(!GetPrivate<Control>(settings, "_filter").Visible && !GetPrivate<Control>(settings, "_custom").Enabled, "Country filter and custom strategies do not apply to a server");
        }
    }
}
