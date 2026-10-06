using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zarp.Core;

static partial class Program
{
    static Authenticode.Result TrustedCloudflare() => new Authenticode.Result
    {
        Trusted = true,
        SignerSubject = "O=\"Cloudflare, Inc.\", C=US",
        SignerSubjectRaw = new X500DistinguishedName("O=\"Cloudflare, Inc.\", C=US").RawData,
    };

    static void SetPrivate(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance).SetValue(target, value);
    static T GetPrivate<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance).GetValue(target);
    static Strategy PlainStrategy(string id) => new Strategy { Id = id, Name = id, Transport = WarpTransport.MasqueH2, Args = "" };

    static void TestReviewRegressions()
    {
        TestSignerStructure();
        TestConfigRecoveryAndCustomIds();
        Wait(TestEngineRegressionsAsync);
        Wait(TestProcessesAndUpdatesAsync);
        Wait(TestDownloadRegressionsAsync);
        TestUiRegressions();
        SetFinder(Missing);
    }

    static void TestSignerStructure()
    {
        var parse = AppAssembly.GetType("Zarp.Core.CertificateNames").GetMethod("Organization");
        string Organization(byte[] der) => (string)parse.Invoke(null, new object[] { der });
        var cloudflare = TrustedCloudflare();
        Check(Organization(cloudflare.SignerSubjectRaw) == "Cloudflare, Inc.", "Read the actual organization OID");
        string spoof = "CN=\"Attacker, O=Cloudflare, Inc.\", O=Attacker LLC, C=US";
        byte[] bad = new X500DistinguishedName(spoof).RawData;
        Check(Organization(bad) == "Attacker LLC", "A quoted CN cannot impersonate the organization");
        Check(!(bool)typeof(WarpInstaller).GetMethod("IsCloudflareSigner", PrivateStatic).Invoke(null, new object[] { spoof }), "Reject the reported CN spoof");
        var decide = typeof(WarpInstaller).GetMethod("CheckSigner", PrivateStatic);
        cloudflare.SignerSubjectRaw = bad;
        Check(Catch(() => decide.Invoke(null, new object[] { cloudflare })) != null, "Display text cannot override certificate DER");
        cloudflare.SignerSubjectRaw = null;
        Check(Catch(() => decide.Invoke(null, new object[] { cloudflare })) != null, "No DER means no verified organization");
        byte[] one = new X500DistinguishedName("O=\"Cloudflare, Inc.\"").RawData;
        byte[] twice = new byte[2 + (one.Length - 2) * 2];
        twice[0] = 0x30; twice[1] = (byte)(twice.Length - 2);
        Array.Copy(one, 2, twice, 2, one.Length - 2);
        Array.Copy(one, 2, twice, one.Length, one.Length - 2);
        Check(Organization(twice) == null, "Reject repeated organization attributes");
        for (int i = 0; i < one.Length; i++)
            Check(Organization(one.Take(i).ToArray()) == null, "Truncated DER fails closed at byte " + i);
        Check(Organization(one.Concat(new byte[] { 0 }).ToArray()) == null, "Reject trailing DER bytes");

        string cli = MakeFakeCli(Path.Combine(_data, "untrusted-locator"), 0);
        var found = new WarpLocator.Result();
        typeof(WarpLocator).GetMethod("Probe", PrivateStatic).Invoke(null, new object[] { found, Path.GetDirectoryName(cli) });
        Check(found.CliPath == null && found.Report.Any(s => s.Contains("untrusted")), "Locator rejects an unsigned CLI");
        SetFinder(() => Found(cli));
        var warp = new Warp();
        var verifier = typeof(Warp).GetField("VerifyCli", PrivateStatic);
        var saved = verifier.GetValue(null);
        try
        {
            verifier.SetValue(null, (Func<string, Authenticode.Result>)Authenticode.Verify);
            Check(Catch(() => Wait(async () => { await warp.Cli("status"); })) != null, "Runtime validation also rejects an unsigned CLI");
            bool locked = false;
            verifier.SetValue(null, (Func<string, Authenticode.Result>)(path =>
            {
                locked = Catch(() => { using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }) is IOException;
                return TrustedCloudflare();
            }));
            Wait(async () => { Check((await warp.Cli("status")).Ok, "Controlled test CLI executes after verification"); });
            Check(locked, "The executable must be protected from replacement during verification");
        }
        finally { verifier.SetValue(null, saved); SetFinder(Missing); }
    }

    static void TestConfigRecoveryAndCustomIds()
    {
        string path = Path.Combine(_data, "atomic.json"), other = Path.Combine(_data, "other.json");
        var a = AppConfig.Load(path); a.SelectedStrategyId = "first"; a.Save();
        var b = AppConfig.Load(other); b.SelectedStrategyId = "other"; b.Save();
        a.SelectedStrategyId = "second"; a.Save();
        Check(AppConfig.Load(path).SelectedStrategyId == "second" && AppConfig.Load(other).SelectedStrategyId == "other", "Each config saves to its own path");
        Check(AppConfig.Load(path + ".bak").SelectedStrategyId == "first", "Atomic replacement keeps the previous config");
        File.WriteAllText(path, "{broken");
        var restored = AppConfig.Load(path);
        Check(restored.SelectedStrategyId == "first", "A corrupt primary restores its backup");
        restored.SelectedStrategyId = "recovered"; restored.Save();
        Check(AppConfig.Load(path).SelectedStrategyId == "recovered" && AppConfig.Load(path + ".bak").SelectedStrategyId == "first", "Recovery must not overwrite the good backup with corruption");
        Check(!Directory.GetFiles(_data, "atomic.json.*.tmp").Any(), "Config saves leave no temporary files");
        string invalid = Path.Combine(_data, "invalid.json");
        File.WriteAllText(invalid, "{\"TestTimeoutSec\":-10,\"StopAfterWorking\":2147483647,\"Results\":{\"null\":null,\"bad\":{\"StrategyId\":\"bad\",\"Ok\":true,\"PingMs\":-1}}}");
        var c = AppConfig.Load(invalid);
        Check(c.TestTimeoutSec == 5 && c.StopAfterWorking == 100 && c.Results.Count == 0, "Normalize malformed settings and null results");
        Check(new TestResult { Ok = true, ConnectMs = int.MaxValue, PingMs = int.MaxValue }.Score == int.MaxValue - 1, "Ranking arithmetic must not overflow");

        var engine = NewEngine();
        string custom = Path.Combine(engine.DataDir, StrategyCatalog.CustomFileName);
        File.WriteAllText(custom, "My One | h2 | --old\nmy-one | h3 | --other\nMy One | h2 | --old\n");
        engine.ReloadStrategies();
        var entries = engine.Strategies.Where(s => s.Custom).ToList();
        Check(entries.Count == 2 && entries.Select(s => s.Id).Distinct().Count() == 2, "Custom identities include content and duplicate profiles are collapsed");
        string original = entries[0].Id;
        engine.Config.Results[original] = new TestResult { StrategyId = original, Ok = true, Confirmed = true };
        File.WriteAllText(custom, "My One | h2 | --changed\n");
        engine.ReloadStrategies();
        Check(engine.Strategies.Single(s => s.Custom).Id != original && !engine.Config.Results.ContainsKey(original), "Editing a profile invalidates its old confirmation");
        engine.Config.SelectedStrategyId = "custom-my-one";
        engine.ReloadStrategies();
        Check(engine.Selected?.Custom == true, "An unambiguous legacy selection migrates");
        File.WriteAllText(custom, "My One | h2 | --changed\nmy-one | h3 | --other\n");
        engine.Config.SelectedStrategyId = "custom-my-one";
        engine.ReloadStrategies();
        Check(engine.Selected == null, "Ambiguous legacy selections must not silently pick a profile");
    }

    sealed class MockWarp
    {
        public readonly Engine Engine;
        public readonly List<string> Calls = new List<string>();
        public string Status = "Disconnected";
        public Func<string, RunResult> Override;
        public MockWarp(params Strategy[] strategies)
        {
            Engine = NewEngine();
            SetPrivate(Engine.Warp, "<CliPath>k__BackingField", "test-only-warp-cli");
            SetPrivate(Engine.Warp, "_transport", WarpTransport.MasqueH2);
            SetPrivate(Engine.Warp, "CommandRunner", (Func<string, int, CancellationToken, Task<RunResult>>)((cmd, timeout, ct) =>
            {
                ct.ThrowIfCancellationRequested(); Calls.Add(cmd);
                var overridden = Override?.Invoke(cmd);
                if (overridden != null) return Task.FromResult(overridden);
                if (cmd == "connect") Status = "Connected";
                if (cmd == "disconnect") Status = "Disconnected";
                return Task.FromResult(new RunResult { ExitCode = 0, Output = cmd.Contains("status") ? "{\"status\":\"" + Status + "\"}" : "OK" });
            }));
            SetPrivate(Engine, "FindForeignVpns", (Func<List<string>>)(() => new List<string>()));
            SetPrivate(Engine, "MeasureTraffic", (Func<int, CancellationToken, Task<int>>)((n, ct) => Task.FromResult(20)));
            typeof(Engine).GetProperty("Strategies").SetValue(Engine, strategies.ToList());
            StubFiles(Engine.Zapret.Dir, "v1.0");
        }
        public void Traffic(Func<int, int> measure) => SetPrivate(Engine, "MeasureTraffic", (Func<int, CancellationToken, Task<int>>)((samples, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(measure(samples)); }));
    }

    static async Task TestEngineRegressionsAsync()
    {
        var s = PlainStrategy("plain");
        var mock = new MockWarp(s);
        mock.Override = cmd => cmd.Contains("masque-options") ? new RunResult { ExitCode = 1, Output = "rejected" } : null;
        Exception failure = null;
        try { await mock.Engine.Warp.SetTransportAsync(WarpTransport.MasqueH3, CancellationToken.None); } catch (IOException e) { failure = e; }
        Check(failure != null && GetPrivate<WarpTransport?>(mock.Engine.Warp, "_transport") == null, "A failed transport change cannot be cached");
        mock.Override = cmd => cmd == "disconnect" ? new RunResult { ExitCode = 1, Output = "rejected" } : null;
        await mock.Engine.DisconnectAsync();
        Check(mock.Engine.State == EngineState.Unknown, "A failed disconnect must not report Idle");

        mock = new MockWarp(s); mock.Traffic(n => -1);
        await mock.Engine.UseStrategyAsync(s);
        Check(mock.Engine.State == EngineState.Idle && mock.Engine.Config.SelectedStrategyId == null && mock.Status == "Disconnected", "Connected without WARP traffic is rejected and cleaned up");
        mock.Traffic(n => 12);
        await mock.Engine.UseStrategyAsync(s);
        Check(mock.Engine.State == EngineState.Connected, "A verified tunnel can become Connected");
        mock.Status = "Disconnected"; await mock.Engine.MonitorAsync();
        Check(mock.Engine.State == EngineState.Idle, "Monitoring notices a dropped tunnel");
        // а в простое фоновая проверка не стирает итог последней операции (например, просьбу выключить сторонний VPN)
        typeof(Engine).GetMethod("SetDetail", PrivateInstance).Invoke(mock.Engine, new object[] { new Msg("detail.vpnOff") });
        await mock.Engine.MonitorAsync();
        Check(mock.Engine.State == EngineState.Idle && mock.Engine.Detail == L.T("detail.vpnOff"), "Background monitoring must not erase the outcome of the last operation");
        mock.Status = "Connected"; mock.Traffic(n => -1); await mock.Engine.MonitorAsync();
        Check(mock.Engine.State == EngineState.Unknown, "Monitoring rejects a tunnel that stopped carrying WARP traffic");
        mock.Traffic(n => 10); SetPrivate(mock.Engine, "_activeStrategy", new Strategy { Id = "dead", Args = "--stub" });
        await mock.Engine.MonitorAsync();
        Check(mock.Engine.State == EngineState.Unknown, "Monitoring notices a missing owned winws process");

        mock = new MockWarp(s); mock.Status = "Connected";
        var monitoring = new TaskCompletionSource<bool>();
        SetPrivate(mock.Engine, "MeasureTraffic", (Func<int, CancellationToken, Task<int>>)(async (n, ct) =>
        {
            monitoring.TrySetResult(true); await Task.Delay(10000, ct); return 10;
        }));
        var observe = mock.Engine.MonitorAsync(); await monitoring.Task;
        await mock.Engine.DisconnectAsync(); await observe;
        Check(mock.Status == "Disconnected" && mock.Engine.State == EngineState.Idle, "User disconnection takes priority over background monitoring");

        mock = new MockWarp(s);
        var release = new TaskCompletionSource<bool>();
        SetPrivate(mock.Engine, "<State>k__BackingField", EngineState.Connected);
        var operation = (Task)typeof(Engine).GetMethod("Run", PrivateInstance).Invoke(mock.Engine,
            new object[] { (Func<CancellationToken, Task>)(ct => release.Task) });
        await mock.Engine.RefreshStateAsync();
        Check(mock.Engine.State == EngineState.Connected && mock.Calls.Count == 0, "Startup refresh cannot overwrite a concurrent operation");
        release.SetResult(true); await operation;

        var strategies = new[] { PlainStrategy("first"), PlainStrategy("second"), PlainStrategy("third") };
        mock = new MockWarp(strategies); mock.Engine.Config.StopAfterWorking = 1;
        var replies = new Queue<int>(new[] { 10, -1, 20, 20 });
        mock.Traffic(n => n == 3 ? replies.Dequeue() : 20);
        await mock.Engine.SearchAsync(false);
        Check(!mock.Engine.Config.Results["first"].Ok && mock.Engine.Config.Results["second"].Confirmed && !mock.Engine.Config.Results.ContainsKey("third"), "Quick search continues past failed rechecks until its quota is confirmed");
        Check(mock.Engine.State == EngineState.Connected && mock.Engine.Config.SelectedStrategyId == "second", "Quick search applies the confirmed candidate");
        var endpoints = mock.Calls.Where(c => c.StartsWith("tunnel endpoint set ")).ToArray();
        Check(endpoints.Length == 4 && endpoints[0] != endpoints[1] && endpoints[2] != endpoints[3], "Each H2 recheck uses a different endpoint from its first pass");
        Check(!mock.Engine.Config.Results["second"].Independent && mock.Engine.Config.Results["second"].EndpointReused, "Reusing the finite H2 pool cannot be advertised as fresh independent tests");

        mock = new MockWarp(s);
        int traffic = 0;
        mock.Traffic(n => { traffic++; mock.Engine.Config.IsolateTests = false; return 10; });
        await mock.Engine.SearchAsync(false);
        Check(mock.Calls.Count(c => c.StartsWith("tunnel endpoint set ")) == 2 && mock.Calls.Contains("tunnel endpoint reset"), "Changing the isolation setting mid-search cannot bypass recheck isolation or cleanup");
        Check(mock.Engine.Config.Results[s.Id].Independent && traffic == 3, "Two fresh endpoint checks and final traffic verification succeed");

        mock = new MockWarp(s); mock.Engine.Config.IsolateTests = false;
        await mock.Engine.SearchAsync(false);
        Check(mock.Engine.Config.Results[s.Id].Confirmed && !mock.Engine.Config.Results[s.Id].Independent, "Tests without endpoint isolation are not independent");

        mock = new MockWarp(s); traffic = 0;
        mock.Override = cmd => cmd.StartsWith("tunnel endpoint set ") ? new RunResult { ExitCode = 1, Output = "no endpoint support" } : null;
        mock.Traffic(n => { traffic++; return 10; });
        await mock.Engine.SearchAsync(false);
        Check(!mock.Engine.Config.Results[s.Id].Ok && traffic == 0 && mock.Calls.Contains("tunnel endpoint reset"), "Rejected endpoint changes cannot produce confirmed results and are cleaned up");

        mock = new MockWarp(s);
        SetPrivate(mock.Engine, "FindForeignVpns", (Func<List<string>>)(() => new List<string> { "test VPN" }));
        mock.Engine.AskContinueWithVpn = (list, searching) => true;
        var previous = new TestResult { StrategyId = s.Id, Ok = true, Confirmed = true, ConnectMs = 900, PingMs = 40 };
        mock.Engine.Config.Results[s.Id] = previous;
        replies = new Queue<int>(new[] { 10, -1 }); mock.Traffic(n => replies.Dequeue());
        await mock.Engine.SearchAsync(false);
        Check(ReferenceEquals(previous, mock.Engine.Config.Results[s.Id]) && mock.Engine.State == EngineState.Idle, "A provisional success followed by a failed VPN recheck preserves the saved confirmation");

        var warp = new Warp();
        foreach (WarpTransport transport in Enum.GetValues(typeof(WarpTransport)))
        {
            string prior = null;
            for (int i = 0; i < 32; i++)
            {
                string endpoint = warp.NextEndpoint(transport, prior);
                Check(endpoint != prior, "Endpoint exclusion works for " + transport);
                prior = endpoint;
            }
        }
    }

    static void StubFiles(string dir, string version, string exe = null)
    {
        Directory.CreateDirectory(Path.Combine(dir, "lua"));
        foreach (string file in new[] { "winws2.exe", "cygwin1.dll", "WinDivert.dll", "WinDivert64.sys", @"lua\zapret-lib.lua", @"lua\zapret-antidpi.lua" })
            File.WriteAllText(Path.Combine(dir, file), "test stub; not a driver");
        if (exe != null) File.Copy(exe, Path.Combine(dir, "winws2.exe"), true);
        File.WriteAllText(Path.Combine(dir, "version.txt"), version);
    }

    static string CompileRegression(string folder, string name, string source)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        using (var compiler = new Microsoft.CSharp.CSharpCodeProvider())
        {
            var options = new System.CodeDom.Compiler.CompilerParameters { GenerateExecutable = true, OutputAssembly = path };
            options.ReferencedAssemblies.Add("System.dll");
            var result = compiler.CompileAssemblyFromSource(options, source);
            if (result.Errors.HasErrors) throw new Exception(result.Errors[0].ToString());
        }
        return path;
    }

    static async Task TestProcessesAndUpdatesAsync()
    {
        string dir = Path.Combine(_data, "process-regressions");
        string pipes = CompileRegression(dir, "pipes.exe", @"using System; using System.Diagnostics; using System.Threading;
class P { static void Main(string[] args) {
if (args.Length > 0) { Thread.Sleep(1500); return; }
Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, ""child"") { UseShellExecute=false, CreateNoWindow=true });
Console.WriteLine(""parent finished""); }}");
        var clock = Stopwatch.StartNew();
        var result = await ProcessUtil.RunAsync(pipes, "", 300);
        Check(result.TimedOut && clock.ElapsedMilliseconds < 1400, "Inherited output pipes must share the process deadline");
        using (var cancel = new CancellationTokenSource(100))
        {
            bool cancelled = false;
            try { await ProcessUtil.RunAsync(pipes, "child", 10000, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Process cancellation is reported as cancellation");
        }

        string bad = MakeFakeExe(Path.Combine(dir, "bad"), "winws2.exe", 1);
        var zapret = new Zapret(Path.Combine(dir, "rollback", "zapret2"));
        StubFiles(zapret.Dir, "v1.0", bad); StubFiles(zapret.Dir + ".new", "v2.0", bad);
        Check(zapret.ApplyPendingUpdate() && zapret.Version == "v2.0", "Apply a pending update while idle");
        StubFiles(zapret.Dir + ".new", "v2.1", bad);
        Check(zapret.ApplyPendingUpdate() && File.ReadAllText(Path.Combine(zapret.Dir + ".old", "version.txt")) == "v1.0", "A second pending update keeps the last verified backup");
        var uses = new Strategy { Id = "bad", Name = "bad", Args = "--test-stub", Transport = WarpTransport.MasqueH2 };
        Check(await zapret.StartAsync(uses, true) != null && zapret.Version == "v1.0", "An update applied earlier still rolls back when it fails to start");
        StubFiles(zapret.Dir + ".new", "v3.0", bad); zapret.ApplyPendingUpdate(); File.Delete(zapret.Exe);
        Check(await zapret.StartAsync(uses, true) != null && zapret.Version == "v1.0", "Missing updated files also trigger rollback");

        string fakeCmd = CompileRegression(Path.Combine(dir, "hijack-source"), "cmd.exe",
            "class P { static void Main() { System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"cmd-started.txt\"), \"unexpected\"); } }");
        string planted = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cmd.exe");
        string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cmd-started.txt");
        Check(!File.Exists(planted) && !File.Exists(marker), "The cmd hijack test must not overwrite existing files");
        File.Copy(fakeCmd, planted);
        try
        {
            await (Task<Msg>)typeof(Zapret).GetMethod("StartOnceAsync", PrivateInstance).Invoke(zapret, new object[] { CancellationToken.None });
            Check(!File.Exists(marker), "A cmd.exe beside the app must never execute");
        }
        finally { zapret.Stop(); File.Delete(planted); if (File.Exists(marker)) File.Delete(marker); }

        string sleeper = CompileRegression(Path.Combine(dir, "sleeper"), "winws2.exe", "class P { static void Main() { System.Threading.Thread.Sleep(30000); } }");
        var mock = new MockWarp(PlainStrategy("cleanup"));
        File.Copy(sleeper, mock.Engine.Zapret.Exe, true);
        using (var process = Process.Start(new ProcessStartInfo(mock.Engine.Zapret.Exe) { UseShellExecute = false, CreateNoWindow = true }))
        {
            mock.Override = cmd => cmd == "disconnect" ? new RunResult { ExitCode = 1, Output = "failure" } : null;
            try
            {
                await (Task)typeof(Engine).GetMethod("Run", PrivateInstance).Invoke(mock.Engine, new object[] { (Func<CancellationToken, Task>)(_ => throw new IOException("operation failed")) });
                Check(process.WaitForExit(2000) && mock.Engine.State == EngineState.Unknown, "An operation exception stops owned winws even if WARP disconnect fails");
            }
            finally { try { if (!process.HasExited) process.Kill(); } catch { } }
        }
        string own = Path.Combine(dir, "legacy", "zapret2", "winws2.exe"), foreign = Path.Combine(dir, "legacy", "other-zapret", "winws2.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(own)); Directory.CreateDirectory(Path.GetDirectoryName(foreign));
        File.Copy(sleeper, own); File.Copy(sleeper, foreign);
        using (var p1 = Process.Start(new ProcessStartInfo(own) { UseShellExecute = false, CreateNoWindow = true }))
        using (var p2 = Process.Start(new ProcessStartInfo(foreign) { UseShellExecute = false, CreateNoWindow = true }))
        {
            try
            {
                AppAssembly.GetType("Zarp.Instances").GetMethod("KillWinws2Under", PrivateStatic).Invoke(null, new object[] { Path.Combine(dir, "legacy") });
                Check(p1.WaitForExit(2000) && !p2.HasExited, "Legacy cleanup must not kill a sibling zapret");
            }
            finally { try { if (!p1.HasExited) p1.Kill(); } catch { } try { if (!p2.HasExited) p2.Kill(); } catch { } }
        }
    }

    sealed class CancellableHttp : HttpMessageHandler
    {
        public readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>();
        public CancellationToken NetworkToken;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            NetworkToken = ct; Entered.TrySetResult(true);
            await Task.Delay(10000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    static async Task TestDownloadRegressionsAsync()
    {
        var handler = new CancellableHttp();
        using (var http = new HttpClient(handler))
        using (var cancel = new CancellationTokenSource())
        {
            var download = (Task)typeof(Zapret).GetMethod("ExtractReleaseAsync", PrivateStatic).Invoke(null,
                new object[] { http, "https://test.invalid/archive.zip", "v1", Path.Combine(_data, "cancel-download"), cancel.Token });
            await handler.Entered.Task; cancel.Cancel();
            bool cancelled = false;
            try { await download; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && handler.NetworkToken.IsCancellationRequested, "Zapret cancellation reaches the underlying HTTP request");
        }
        using (var server = new FakeServer(new byte[1024]))
        {
            string target = Path.Combine(_data, "chunked.bin");
            bool rejected = false;
            try
            {
                await (Task<string>)typeof(WarpInstaller).GetMethod("DownloadAsync", PrivateStatic).Invoke(null,
                    new object[] { server.Url("/chunked"), target, 1L, 16L, TimeSpan.FromSeconds(3), null, CancellationToken.None });
            }
            catch { rejected = true; }
            Check(rejected && !File.Exists(target) && !Directory.GetFiles(_data, "chunked.bin.*.part").Any(), "Chunked responses cannot bypass the size limit or leave partial files");
        }
    }

    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
    static void TestUiRegressions()
    {
        var mock = new MockWarp(PlainStrategy("autostart"));
        mock.Engine.Config.SelectedStrategyId = "autostart";
        using (var hidden = NewForm("MainForm", mock.Engine, true, false))
        {
            PositionOffscreen(hidden); hidden.Show();
            PumpUntil(() => GetPrivate<bool>(hidden, "_initialized"), "Autostart initialization");
            Check(!hidden.Visible && !mock.Calls.Contains("connect"), "Autostart stays in the tray without overriding AutoConnectOnStart=false");
            Wait(() => mock.Engine.ShutdownAsync());
        }
        using (var settings = NewForm("SettingsForm", NewEngine()))
        {
            PositionOffscreen(settings); settings.Show();
            var toggle = GetPrivate<Control>(settings, "_autostart");
            PumpUntil(() => toggle.Enabled, "Autostart query");
            var checkedProperty = toggle.GetType().GetProperty("Checked");
            bool requested = !(bool)checkedProperty.GetValue(toggle);
            int writes = 0; var pending = new TaskCompletionSource<bool>();
            SetPrivate(settings, "SetAutostart", (Func<bool, string, Task<bool>>)((enabled, path) => { writes++; return pending.Task; }));
            SetPrivate(settings, "QueryAutostart", (Func<Task<bool>>)(() => Task.FromResult(requested)));
            checkedProperty.SetValue(toggle, requested);
            Check(!toggle.Enabled && writes == 1, "An autostart update disables concurrent user toggles");
            checkedProperty.SetValue(toggle, !requested);
            Check(writes == 1, "A second event cannot start a competing scheduler write");
            pending.SetResult(true); PumpUntil(() => toggle.Enabled, "Autostart update");
            Check((bool)checkedProperty.GetValue(toggle) == requested, "The switch reconciles the actual scheduler state");
        }
        TestLogLayout();
        using (var form = NewMain(NewEngine()))
        {
            var button = GetPrivate<Button>(form, "_power"); int clicked = 0;
            GetPrivate<Engine>(form, "_engine").AskInstallWarp = () => false;
            button.Click += (s, e) => clicked++;
            ((IButtonControl)button).PerformClick();
            Check(clicked == 1 && button.AccessibleRole == AccessibleRole.PushButton && !string.IsNullOrWhiteSpace(button.AccessibleName), "The power control supports standard button activation and accessibility");
            var closing = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
            form.GetType().GetMethod("OnFormClosing", PrivateInstance).Invoke(form, new object[] { closing });
            Check(!closing.Cancel, "Windows session ending is not cancelled");
        }
        var create = AppAssembly.GetType("Zarp.UI.Theme").GetMethod("AppIcon");
        using (var warmup = (Icon)create.Invoke(null, new object[] { 32, Color.Orange })) { }
        int before = GetGuiResources(Process.GetCurrentProcess().Handle, 1);
        for (int i = 0; i < 32; i++) using ((Icon)create.Invoke(null, new object[] { 32, Color.Orange })) { }
        Check(GetGuiResources(Process.GetCurrentProcess().Handle, 1) - before < 5, "Creating and disposing icons must not leak USER handles");
    }

    static void TestLogLayout()
    {
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
        foreach (int adjustment in new[] { 0, -100, 7 })
        using (var form = NewMain(NewEngine()))
        {
            form.Scale(new SizeF(scale, scale));
            // Reproduce a compact window whose size differs from the child-control scale.
            // The negative adjustment also covers a screen that clips the initial compact layout.
            form.ClientSize = new Size(form.ClientSize.Width, form.ClientSize.Height + adjustment);
            var compact = form.ClientSize;
            var hint = GetPrivate<Label>(form, "_hint");
            var hintBounds = hint.Bounds;
            var link = GetPrivate<LinkLabel>(form, "_logToggle");
            var linkBounds = link.Bounds;
            var log = GetPrivate<TextBox>(form, "_log");
            var toggle = form.GetType().GetMethod("ToggleLog", PrivateInstance);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                // The log's expanded state must also work while the form is hidden in the tray.
                if (cycle == 1) form.Hide();
                toggle.Invoke(form, null);
                string context = $"scale={scale}, adjustment={adjustment}, cycle={cycle}, compact={compact}, actual={form.ClientSize}, logBottom={log.Bottom}, maxTrack={SystemInformation.MaxWindowTrackSize}";
                Check(form.ClientSize.Height > compact.Height && log.Bottom < form.ClientSize.Height, "The expanded log fits: " + context);
                Check(GetPrivate<bool>(form, "_logExpanded"), "The log is expanded: " + context);
                toggle.Invoke(form, null);
                context = $"scale={scale}, adjustment={adjustment}, cycle={cycle}, expected={compact}, actual={form.ClientSize}, hint={hint.Bounds}";
                Check(form.ClientSize == compact, "Collapsing restores the actual compact size: " + context);
                // Form.Scale can already clip a top-level window on a small CI desktop. The toggle
                // must preserve its original layout, not assume that the desktop fits a 200% form.
                Check(hint.Bounds == hintBounds && link.Bounds == linkBounds, "Toggling must not move the compact controls: " + context);
                Check(!log.Visible && !GetPrivate<bool>(form, "_logExpanded"), "The log is collapsed: " + context);
            }
        }

        using (var form = NewMain(NewEngine()))
        using (var reference = NewMain(NewEngine()))
        {
            var compact = new Size(form.ClientSize.Width, form.ClientSize.Height + 7);
            form.ClientSize = reference.ClientSize = compact;
            var toggle = form.GetType().GetMethod("ToggleLog", PrivateInstance);
            toggle.Invoke(form, null);
            form.Scale(new SizeF(1.25f, 1.25f));
            reference.Scale(new SizeF(1.25f, 1.25f));
            toggle.Invoke(form, null);
            Check(form.ClientSize == reference.ClientSize, $"Scaling while the log is open preserves the scaled compact size: expected={reference.ClientSize}, actual={form.ClientSize}");
        }
    }

    static void PumpUntil(Func<bool> done, string description)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(), description + " must finish");
    }
}
