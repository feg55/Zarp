using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zarp.Core;

// Страны для стратегий, свои эндпоинты WARP. Ничего из этого не запускает настоящие WARP и WinDivert.
static partial class Program
{
    static Strategy Tagged(string id, string args, params string[] countries) => new Strategy
    {
        Id = id, Name = id, Transport = WarpTransport.MasqueH2, Args = args, Countries = countries,
    };

    const string SplitArgs = "--payload=tls_client_hello --lua-desync=multisplit:pos=1";

    static void TestStrategyCountries()
    {
        var all = StrategyCatalog.BuiltInStrategies.ToList();
        foreach (var s in all)
        {
            Check(s.Countries.Length > 0 && s.Countries.All(c => c == "ru" || c == "ir" || c == "cn"),
                s.Id + ": every built-in strategy needs country tags");
            bool russian = s.Args.Contains("vk") || s.Args.Contains("gosuslugi");
            Check(russian == (s.Countries.Length == 1 && s.Countries[0] == "ru"),
                s.Id + ": fakes of Russian services are tagged for Russia only, the rest for all three countries");
        }

        // новые варианты из списка Android: разбиение по host, sld, endhost и fake google x12
        var added = new Dictionary<string, string>
        {
            ["warp-t-split-host"] = "--payload=tls_client_hello --lua-desync=multisplit:pos=1,host",
            ["warp-t-split-sld"] = "--payload=tls_client_hello --lua-desync=multisplit:pos=1,sld",
            ["warp-t-split-endhost"] = "--payload=tls_client_hello --lua-desync=multisplit:pos=1,endhost",
            ["warp-q-google12"] = "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=12",
        };
        foreach (var pair in added)
        {
            var s = all.SingleOrDefault(x => x.Id == pair.Key);
            Check(s != null && s.Args == pair.Value, "Missing or changed strategy " + pair.Key);
            Check(s.Countries.SequenceEqual(new[] { "ru", "ir", "cn" }), pair.Key + ": generic variants are offered in all three countries");
        }
        Check(all.Single(s => s.Id == "warp-t-split-host").Transport == WarpTransport.MasqueH2 && all.Single(s => s.Id == "warp-q-google12").Transport == WarpTransport.MasqueH3,
            "Added strategies must use the transport that matches their payload");

        Check(StrategyFilter.Apply(all, "all").Count == all.Count, "All keeps every strategy");
        foreach (var country in new[] { "ru", "ir", "cn" })
        {
            var visible = StrategyFilter.Apply(all, country);
            Check(visible.Count > 0 && visible.All(s => s.UsesZapret && s.Countries.Contains(country)), country + ": only tagged strategies with desync remain");
        }
        Check(!StrategyFilter.Apply(all, "cn").Any(s => s.Args.Contains("vk") || s.Args.Contains("gosuslugi")) &&
              StrategyFilter.Apply(all, "ru").Any(s => s.Args.Contains("vk")), "Russian fakes belong to Russia only");

        // несколько стран образуют объединение
        var union = new HashSet<string>(StrategyFilter.Apply(all, "ru").Concat(StrategyFilter.Apply(all, "cn")).Select(s => s.Id));
        Check(StrategyFilter.Apply(all, "ru,cn").Select(s => s.Id).ToHashSet().SetEquals(union), "Several countries form a union");
        Check(StrategyFilter.Toggle("cn", "ru") == "ru,cn" && StrategyFilter.Toggle("ru,cn", "ru") == "cn" && StrategyFilter.Toggle("cn", "cn") == "all",
            "Toggle adds, removes and falls back to All");
        Check(StrategyFilter.Toggle("ru,cn", "all") == "all" && StrategyFilter.Toggle("all", "ir") == "ir", "All clears the filter");
        Check(StrategyFilter.Normalize("CN, ru ,xx,all") == "ru,cn" && StrategyFilter.Normalize(null) == "all" && StrategyFilter.Normalize("") == "all" && StrategyFilter.Normalize("xx") == "all",
            "Stored values are normalized");
        var plain = Tagged("plain", "", "ru");
        Check(!StrategyFilter.Matches(plain, "ru") && StrategyFilter.Matches(plain, "all"),
            "Plain WARP without masking is excluded as soon as a country is chosen");
        Check(StrategyFilter.Apply(new[] { plain }, "ru").Count == 0 && StrategyFilter.Apply(new[] { plain }, "all").Count == 1,
            "The list filter and the single-strategy check must agree about plain WARP");

        // собственный сервер фильтр не касается
        Check(ProxyProfile.TryParse("trojan://secret@proxy.example:443", out var profile) && StrategyFilter.Apply(new[] { profile.ToStrategy() }, "cn").Count == 1 &&
              StrategyFilter.Matches(profile.ToStrategy(), "cn") && StrategyFilter.Matches(profile.ToStrategy(), "ru,ir"),
            "A custom server is never filtered by country");

        // страны назначаются своим стратегиям четвёртым полем
        var engine = NewEngine();
        string file = Path.Combine(engine.DataDir, StrategyCatalog.CustomFileName);
        File.WriteAllText(file,
            "Mine | h2 | " + SplitArgs + " | cn, IR ,xx\n" +
            "Untagged | h2 | " + SplitArgs + "\n" +
            "Everywhere | h2 | " + SplitArgs + ",sld | ru,ir,cn,all\n" +
            "Plain | h3 | | ru\n");
        engine.ReloadStrategies();
        var custom = engine.Strategies.Where(s => s.Custom).ToDictionary(s => s.Name);
        Check(custom.Count == 4, "All four custom lines must load");
        Check(custom["★ Mine"].Countries.SequenceEqual(new[] { "ir", "cn" }), "The fourth field lists countries; unknown codes are ignored");
        Check(custom["★ Untagged"].Countries.Length == 0 && custom["★ Everywhere"].Countries.Length == 3, "Old three-field lines carry no country");
        var inCn = StrategyFilter.Apply(engine.Strategies.Where(s => s.Custom), "cn").Select(s => s.Name).ToList();
        Check(inCn.SequenceEqual(new[] { "★ Mine", "★ Everywhere" }), "Under a country only tagged custom strategies remain, got " + string.Join(", ", inCn));
        Check(StrategyFilter.Apply(engine.Strategies.Where(s => s.Custom), "all").Count() == 4, "Untagged and plain lines stay available under All");

        string id = custom["★ Mine"].Id;
        File.WriteAllText(file, "Mine | h2 | " + SplitArgs + "\n");
        engine.ReloadStrategies();
        Check(engine.Strategies.Single(s => s.Custom).Id == id, "Adding or removing countries keeps the strategy identity, so saved results survive");
    }

    static void TestEndpointParser()
    {
        Check(EndpointParser.Parse("# comment\n162.159.198.2:8443\n[2606:4700::1]:443\r\n162.159.198.2:8443 # again").SequenceEqual(
                new[] { "162.159.198.2:8443", "[2606:4700::1]:443" }),
            "Comments, blank lines and repeats are dropped, order is kept");
        Check(EndpointParser.Parse(" \n# defaults").Count == 0 && EndpointParser.Parse(null).Count == 0, "An empty list means WARP defaults");
        foreach (var bad in new[] { "example.com:443", "1.2.3.4:0", "1.2.3.4:65536", "127.1:443", "256.1.1.1:443", "1.2.3.4", "1.2.3.4:443x",
                                    "[abc]:443", "::1:443", "1.2.3.4:443:80", "01.2.3.4:443", "[::1]", "[::1]443", "1.2.3.4:-1", "[fe80::1%5]:443" })
            Check(!EndpointParser.TryParse(bad, out _) && Catch(() => EndpointParser.Parse(bad)) is FormatException, "Must be rejected: " + bad);
        Check(EndpointParser.TryParse("0.0.0.0:1\n255.255.255.255:65535", out var edges) && edges.Count == 2, "Edge values are valid");
        Check(EndpointParser.Addresses(new[] { "1.2.3.4:443", "[2606:4700::1]:8443", "1.2.3.4:500" }).SequenceEqual(new[] { "1.2.3.4", "2606:4700::1" }),
            "Addresses drop ports and repeats");
    }

    static void TestCustomEndpointPool()
    {
        var own = new[] { "1.2.3.4:443", "5.6.7.8:8443" };
        foreach (WarpTransport transport in Enum.GetValues(typeof(WarpTransport)))
        {
            var warp = new Warp();
            string first = warp.NextEndpoint(transport, null, own);
            string second = warp.NextEndpoint(transport, first, own);
            string third = warp.NextEndpoint(transport, second, own);
            Check(own.Contains(first) && own.Contains(second) && first != second, transport + ": a recheck uses another own address");
            Check(third == first && warp.EndpointWasReused, transport + ": a finite pool is reused and the reuse is reported");
            Check(!own.Contains(new Warp().NextEndpoint(transport)), transport + ": without own addresses the defaults stay in use");
        }
        // свои адреса идут по очереди, наименее использованный первым
        var turns = new Warp();
        var pool = new[] { "1.1.1.1:443", "2.2.2.2:443", "3.3.3.3:443" };
        Check(Enumerable.Range(0, 4).Select(_ => turns.NextEndpoint(WarpTransport.MasqueH3, null, pool)).SequenceEqual(new[] { pool[0], pool[1], pool[2], pool[0] }),
            "Own addresses are used in turn, the least used first");
        // адрес первой проверки пропускается, даже если он самый свободный
        var skewed = new Warp();
        for (int i = 0; i < 3; i++) skewed.NextEndpoint(WarpTransport.MasqueH3, null, new[] { "5.6.7.8:8443" });
        Check(skewed.NextEndpoint(WarpTransport.MasqueH3, "1.2.3.4:443", own) == "5.6.7.8:8443",
            "The address of the first test is skipped even when it is the least used one");

        var single = new Warp();
        string one = single.NextEndpoint(WarpTransport.MasqueH3, null, new[] { "9.9.9.9:443" });
        Check(one == "9.9.9.9:443" && !single.EndpointWasReused, "A single address is used as it is");
        Check(single.NextEndpoint(WarpTransport.MasqueH3, one, new[] { "9.9.9.9:443" }) == one && single.EndpointWasReused,
            "With one address a recheck cannot be isolated: the address is taken again and marked as reused");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int CompileFilter([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, IntPtr compiled, uint length, out IntPtr error, out uint position);

    /// <summary>Разбор фильтра самим WinDivert.dll: помощник не открывает драйвер и не трогает сеть. Возвращает текст ошибки или null.</summary>
    static string WinDivertSyntaxError(string dllPath, string filter)
    {
        IntPtr module = LoadLibraryW(dllPath);
        if (module == IntPtr.Zero) throw new TestSkippedException("WinDivert.dll could not be loaded: " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        try
        {
            IntPtr function = GetProcAddress(module, "WinDivertHelperCompileFilter");
            if (function == IntPtr.Zero) throw new TestSkippedException("WinDivertHelperCompileFilter is not exported");
            var compile = (CompileFilter)Marshal.GetDelegateForFunctionPointer(function, typeof(CompileFilter));
            if (compile(filter, 0 /* WINDIVERT_LAYER_NETWORK */, IntPtr.Zero, 0, out var error, out uint position) != 0) return null;
            return Marshal.PtrToStringAnsi(error) + " at " + position;
        }
        finally { FreeLibrary(module); }
    }

    static void TestWarpFilterWithOwnEndpoints() => WithEmbeddedZapret(real =>
    {
        if (!Environment.Is64BitProcess) throw new TestSkippedException("The 64-bit WinDivert.dll cannot be loaded into a 32-bit test process");
        var s = StrategyCatalog.BuiltInStrategies.First(x => x.Transport == WarpTransport.MasqueH2);
        string Raw(Zapret z, bool restrict) => z.BuildArgs(s, restrict).Where(a => a.StartsWith("--wf-raw-filter=")).Select(a => a.Substring("--wf-raw-filter=".Length)).SingleOrDefault();

        var zapret = new Zapret(Path.Combine(_data, "filter-only"));
        string basic = Raw(zapret, true);
        Check(basic != null && Raw(zapret, false) == null, "The address filter is added only when interception is restricted");
        zapret.ExtraAddresses = EndpointParser.Addresses(EndpointParser.Parse("203.0.113.7:443\n[2001:db8::7]:8443"));
        string extended = Raw(zapret, true);
        Check(!basic.Contains("203.0.113.7") && extended.Contains("ip.DstAddr==203.0.113.7") && extended.Contains("ip.SrcAddr==203.0.113.7") &&
              extended.Contains("ipv6.DstAddr==2001:db8::7") && extended.Contains("ipv6.SrcAddr==2001:db8::7"),
            "Own endpoints must be intercepted in both directions, IPv4 and IPv6: " + extended);
        zapret.ExtraAddresses = new[] { "not-an-ip", "198.51.100.1", "198.51.100.1" };
        string odd = Raw(zapret, true);
        Check(!odd.Contains("not-an-ip") && odd.Split(new[] { "ip.DstAddr==198.51.100.1" }, StringSplitOptions.None).Length == 2, "Invalid and repeated addresses are ignored");

        string dll = Path.Combine(real.Dir, "WinDivert.dll");
        foreach (var filter in new[] { basic, extended, odd })
            Check(WinDivertSyntaxError(dll, filter) == null, "WinDivert must accept the filter: " + filter + " -> " + WinDivertSyntaxError(dll, filter));
        Check(WinDivertSyntaxError(dll, "ip.DstAddr==") != null, "The syntax check itself must reject a broken filter");
    });

    /// <summary>Подмена winws2: процесс, который просто живёт, чтобы движок считал, что zapret2 запущен.</summary>
    static void UseSleepingWinws(Engine engine, string folder)
    {
        string sleeper = CompileRegression(folder, "winws2.exe", "class P { static void Main() { System.Threading.Thread.Sleep(60000); } }");
        File.Copy(sleeper, engine.Zapret.Exe, true);
    }

    static async Task TestCountryFilterInEngineAsync()
    {
        var ru = Tagged("scan-ru", SplitArgs, "ru");
        var cn = Tagged("scan-cn", SplitArgs, "cn");
        var both = Tagged("scan-both", SplitArgs, "ru", "cn");
        var plain = PlainStrategy("scan-plain");
        var mock = new MockWarp(ru, cn, both, plain);
        UseSleepingWinws(mock.Engine, Path.Combine(_data, "filter-winws"));
        var engine = mock.Engine;
        engine.Config.TestTimeoutSec = 5;
        try
        {
            Check(engine.Candidates.Count == 4, "Without a filter every strategy is a candidate");
            engine.Config.StrategyCountry = "cn";
            Check(engine.Candidates.Select(s => s.Id).SequenceEqual(new[] { "scan-cn", "scan-both" }), "Candidates follow the filter");

            // сохранённые результаты исключённых стратегий не участвуют ни в выборе, ни в подключении
            foreach (var s in engine.Strategies)
                engine.Config.Results[s.Id] = new TestResult { StrategyId = s.Id, Ok = true, Confirmed = true, ConnectMs = 500, PingMs = 20 };
            engine.Config.Results["scan-ru"].ConnectMs = 1; // самый быстрый, но не для Китая
            engine.Config.SelectedStrategyId = "scan-ru";
            var confirmed = (List<Strategy>)typeof(Engine).GetMethod("ConfirmedStrategies", PrivateInstance).Invoke(engine, new object[] { null });
            Check(confirmed.Select(s => s.Id).SequenceEqual(new[] { "scan-cn", "scan-both" }), "Saved results of excluded strategies are not used: " + string.Join(", ", confirmed.Select(s => s.Id)));
            Check(engine.Selected == null, "A selected strategy excluded by the filter is not selected any more");
            mock.Calls.Clear();
            await engine.UseStrategyAsync(ru);
            Check(mock.Calls.Count == 0 && engine.State == EngineState.Idle, "An excluded strategy cannot be applied");
            await engine.TestStrategiesAsync(new[] { ru });
            Check(mock.Calls.Count == 0, "An excluded strategy cannot be tested");

            // полный поиск проверяет только подходящие
            engine.Config.Results.Clear();
            engine.Config.SelectedStrategyId = null;
            await engine.SearchAsync(full: true);
            Check(engine.Config.Results.Keys.OrderBy(k => k).SequenceEqual(new[] { "scan-both", "scan-cn" }), "A full scan tests only candidates: " + string.Join(", ", engine.Config.Results.Keys));
            Check(engine.State == EngineState.Connected && (engine.Config.SelectedStrategyId == "scan-cn" || engine.Config.SelectedStrategyId == "scan-both"),
                "The scan connects with a candidate: " + engine.State + " " + engine.Config.SelectedStrategyId);

            // быстрый поиск и подключение с нуля - тоже
            await engine.DisconnectAsync();
            engine.Config.StrategyCountry = "ru";
            engine.Config.Results.Clear();
            engine.Config.SelectedStrategyId = null;
            await engine.ConnectAsync();
            Check(engine.Config.Results.Keys.All(k => k == "scan-ru" || k == "scan-both") && engine.Config.Results.ContainsKey("scan-ru"),
                "A quick scan under Russia tests only Russian candidates: " + string.Join(", ", engine.Config.Results.Keys));

            // стратегия без обхода пока выбрана страна не проверяется, а при «All» возвращается
            await engine.DisconnectAsync();
            engine.Config.Results.Clear();
            await engine.TestStrategiesAsync(new[] { plain });
            Check(engine.Config.Results.Count == 0, "Plain WARP is not tested while a country is chosen");
            engine.Config.StrategyCountry = "all";
            await engine.TestStrategiesAsync(new[] { plain });
            Check(engine.Config.Results.ContainsKey("scan-plain"), "All brings strategies without desync back: " + string.Join(", ", engine.Config.Results.Keys));
        }
        finally { engine.Zapret.Stop(); }
    }

    static async Task TestOwnEndpointsInEngineAsync()
    {
        var plain = PlainStrategy("own");
        List<string> Sets(MockWarp m) => m.Calls.Where(c => c.StartsWith("tunnel endpoint set ")).Select(c => c.Substring("tunnel endpoint set ".Length)).ToList();

        // два своих адреса: проверки идут только на них, перепроверка на другом, подключение - на проверенном
        var mock = new MockWarp(plain);
        mock.Engine.Config.CustomEndpoints = "# my pool\n1.2.3.4:443\n[2606:4700::9]:8443\n";
        await mock.Engine.SearchAsync(false);
        var sets = Sets(mock);
        Check(sets.Count == 3 && sets.All(s => s == "1.2.3.4:443" || s == "[2606:4700::9]:8443"), "Only own addresses are used: " + string.Join(" | ", sets));
        Check(sets[0] != sets[1], "The recheck goes to another own address");
        Check(sets[2] == sets[1] && mock.Engine.Config.Results["own"].Endpoint == sets[1], "The connection pins the address that passed the last check");
        Check(mock.Engine.Config.Results["own"].Independent && !mock.Engine.Config.Results["own"].EndpointReused, "Two different own addresses count as independent checks");
        int reset = mock.Calls.LastIndexOf("tunnel endpoint reset"), connect = mock.Calls.LastIndexOf("connect");
        Check(reset >= 0 && reset < mock.Calls.LastIndexOf("tunnel endpoint set " + sets[2]) && connect > mock.Calls.LastIndexOf("tunnel endpoint set " + sets[2]),
            "The search returns WARP to automatic selection, then the pin is set before connecting");
        Check(mock.Engine.Zapret.ExtraAddresses.SequenceEqual(new[] { "1.2.3.4", "2606:4700::9" }), "Own addresses must reach the zapret2 interception filter");
        mock.Calls.Clear();
        await mock.Engine.DisconnectAsync();
        Check(mock.Calls.Contains("tunnel endpoint reset"), "Disconnecting returns WARP to its own endpoint choice");

        // подключение с сохранённой стратегией берёт адрес из результата, а если его убрали из списка - первый
        mock.Calls.Clear();
        mock.Engine.Config.CustomEndpoints = "5.6.7.8:443\n9.9.9.9:443";
        await mock.Engine.ConnectAsync();
        Check(Sets(mock).SequenceEqual(new[] { "5.6.7.8:443" }), "A removed address is replaced by the first one: " + string.Join(" | ", Sets(mock)));

        // один адрес: изолировать нечем, и это честно сказано в результате
        mock = new MockWarp(plain);
        mock.Engine.Config.CustomEndpoints = "7.7.7.7:443";
        await mock.Engine.SearchAsync(false);
        Check(Sets(mock).All(s => s == "7.7.7.7:443"), "A single own address is the only one used");
        var result = mock.Engine.Config.Results["own"];
        Check(result.Confirmed && result.EndpointReused && !result.Independent, "With one address the recheck is confirmed but not independent");

        // без изоляции проверки идут на первый свой адрес, а без своих адресов ничего не закрепляется
        mock = new MockWarp(plain);
        mock.Engine.Config.IsolateTests = false;
        mock.Engine.Config.CustomEndpoints = "1.1.1.1:443\n2.2.2.2:443";
        await mock.Engine.SearchAsync(false);
        Check(Sets(mock).Count == 3 && Sets(mock).All(s => s == "1.1.1.1:443"), "Without isolation the first own address is used: " + string.Join(" | ", Sets(mock)));
        mock = new MockWarp(plain);
        mock.Engine.Config.IsolateTests = false;
        await mock.Engine.SearchAsync(false);
        Check(Sets(mock).Count == 0, "Without own addresses nothing is pinned");

        // испорченный список (файл настроек правили руками) не запускает ничего
        mock = new MockWarp(plain);
        mock.Engine.Config.CustomEndpoints = "not an endpoint";
        await mock.Engine.ConnectAsync();
        Check(mock.Calls.Count == 0 && mock.Engine.State == EngineState.Idle && mock.Engine.Detail == L.T("detail.endpointsInvalid"),
            "An invalid endpoint list stops before any warp-cli command: " + mock.Engine.Detail);

        // смена эндпоинтов или сервера сбрасывает проверки и выбор; то же значение их не трогает.
        // Настоящий движок: ApplyConnection перечитывает список стратегий (встроенные + свои).
        var engine = NewEngine();
        engine.Config.Results["warp-q-google6"] = new TestResult { StrategyId = "warp-q-google6", Ok = true, Confirmed = true };
        engine.Config.SelectedStrategyId = "warp-q-google6";
        Check(engine.ApplyConnection("", false, "") && engine.Selected != null && engine.Config.Results.Count == 1, "Saving unchanged values keeps results and selection");
        Check(engine.ApplyConnection("1.2.3.4:443", false, "") && engine.Config.Results.Count == 0 && engine.Config.SelectedStrategyId == null &&
              engine.Config.CustomEndpoints == "1.2.3.4:443" && engine.Zapret.ExtraAddresses.SequenceEqual(new[] { "1.2.3.4" }),
            "New endpoints clear results and selection");
        Check(AppConfig.Load(Path.Combine(engine.DataDir, "zarp.json")).CustomEndpoints == "1.2.3.4:443", "Endpoints are saved");
        SetPrivate(engine, "<State>k__BackingField", EngineState.Searching);
        Check(!engine.ApplyConnection("", false, ""), "Connection settings cannot change during an operation");
    }
}
