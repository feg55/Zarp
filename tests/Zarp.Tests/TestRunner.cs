using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using Zarp.Core;

static partial class Program
{
    sealed class TestCase
    {
        public string Name;
        public Action Body;
        public int TimeoutMs = 120000;
    }

    sealed class TestAssertionException : Exception
    {
        public readonly string File;
        public readonly int Line;
        public TestAssertionException(string message, string file, int line) : base(message)
        { File = file; Line = line; }
    }

    sealed class TestSkippedException : Exception
    {
        public TestSkippedException(string message) : base(message) { }
    }

    sealed class CaseResult
    {
        public string Name, Status, Details = "", File = "", Directory;
        public int Checks, Line;
        public double Seconds;
        public string Stdout = "", Stderr = "";
    }

    static List<TestCase> TestCases()
    {
        var tests = new List<TestCase>();
        void Add(string name, Action body) => tests.Add(new TestCase { Name = name, Body = body });
        Add(nameof(TestRunnerReporting), TestRunnerReporting);
        Add(nameof(TestConfig), TestConfig);
        Add(nameof(TestVpnDetection), TestVpnDetection);
        Add(nameof(TestBuiltInStrategies), TestBuiltInStrategies);
        Add(nameof(TestPackedStrategyFiles), TestPackedStrategyFiles);
        Add(nameof(TestLoaderRetry), TestLoaderRetry);
        Add(nameof(TestUnknownWinwsOption), TestUnknownWinwsOption);
        foreach (bool restrict in new[] { true, false })
        foreach (var strategy in StrategyCatalog.BuiltInStrategies.Where(s => s.UsesZapret))
            Add($"TestStrategyDryRun[{strategy.Id},restrict={restrict}]", () => TestStrategyDryRun(strategy, restrict));
        Add(nameof(TestMissingStrategyBlob), TestMissingStrategyBlob);
        Add(nameof(TestStrategyLuaSymbols), TestStrategyLuaSymbols);
        Add(nameof(TestRestoreEmbeddedFiles), TestRestoreEmbeddedFiles);
        Add(nameof(TestSearchRules), TestSearchRules);
        Add(nameof(TestStrategyCountries), TestStrategyCountries);
        Add(nameof(TestEndpointParser), TestEndpointParser);
        Add(nameof(TestCustomEndpointPool), TestCustomEndpointPool);
        Add(nameof(TestWarpFilterWithOwnEndpoints), TestWarpFilterWithOwnEndpoints);
        Add(nameof(TestCountryFilterInEngineAsync), () => Wait(TestCountryFilterInEngineAsync));
        Add(nameof(TestOwnEndpointsInEngineAsync), () => Wait(TestOwnEndpointsInEngineAsync));
        Add(nameof(TestRouteRuleParsing), TestRouteRuleParsing);
        Add(nameof(TestRoutingSettings), TestRoutingSettings);
        Add(nameof(TestRouteCompile), TestRouteCompile);
        Add(nameof(TestGeoDatValidation), TestGeoDatValidation);
        Add(nameof(TestGeoDataDownload), TestGeoDataDownload);
        Add(nameof(TestInstalledApps), TestInstalledApps);
        Add(nameof(TestRoutingForm), TestRoutingForm);
        Add(nameof(TestPresetForm), TestPresetForm);
        Add(nameof(TestGeoSourcesForm), TestGeoSourcesForm);
        Add(nameof(TestAppsForm), TestAppsForm);
        Add(nameof(TestProxyProfiles), TestProxyProfiles);
        Add(nameof(TestProxyConfig), TestProxyConfig);
        Add(nameof(TestProxyModeEngineAsync), () => Wait(TestProxyModeEngineAsync));
        Add(nameof(TestProxyModeFailuresAsync), () => Wait(TestProxyModeFailuresAsync));
        Add(nameof(TestProxyModeWindows), TestProxyModeWindows);
        Add(nameof(TestConnectionForm), TestConnectionForm);
        Add(nameof(TestCountryFilterControl), TestCountryFilterControl);
        Add(nameof(TestSingBoxLaunchRetry), TestSingBoxLaunchRetry);
        Add(nameof(TestSingBoxConfigsAccepted), TestSingBoxConfigsAccepted);
        Add(nameof(TestProbeClient), TestProbeClient);
        Add(nameof(TestProxyMeasure), TestProxyMeasure);
        Add(nameof(TestSingBoxTamper), TestSingBoxTamper);
        tests.Add(new TestCase { Name = nameof(TestRoutingEndToEnd), Body = TestRoutingEndToEnd, TimeoutMs = 240000 });
        foreach (var variant in new[] { "vless", "trojan", "hysteria2", "vless-ws", "trojan-grpc", "vless-httpupgrade" })
            Add($"TestSingBoxLoopback[{variant}]", () => TestSingBoxLoopback(variant));
        Add(nameof(TestForeignVpnPolicy), TestForeignVpnPolicy);
        Add(nameof(TestDialog), TestDialog);
        Add("TestClose[cancel=True,remember=True,tray=True]", () => TestClose(true, true, true));
        foreach (bool tray in new[] { true, false })
        foreach (bool remember in new[] { false, true })
            Add($"TestClose[cancel=False,remember={remember},tray={tray}]", () => TestClose(false, remember, tray));
        foreach (bool tray in new[] { true, false })
            Add($"TestSavedAction[tray={tray}]", () => TestSavedAction(tray));
        Add(nameof(TestExplicitExit), TestExplicitExit);
        Add(nameof(TestExitDuringPrompt), TestExitDuringPrompt);
        Add(nameof(TestShutdownDuringOperation), TestShutdownDuringOperation);
        Add(nameof(TestSettings), TestSettings);
        Add(nameof(TestLocalization), TestLocalization);
        Add(nameof(TestKeysUsedInCode), TestKeysUsedInCode);
        Add(nameof(TestWarpLocator), TestWarpLocator);
        Add(nameof(TestWarpInstaller), TestWarpInstaller);
        Add(nameof(TestOtherPublisherSignature), TestOtherPublisherSignature);
        Add(nameof(TestWarpDownload), TestWarpDownload);
        Add(nameof(TestEnsureWarp), TestEnsureWarp);
        Add(nameof(TestInstallHint), TestInstallHint);
        Add(nameof(TestSignerStructure), TestSignerStructure);
        Add(nameof(TestConfigRecoveryAndCustomIds), TestConfigRecoveryAndCustomIds);
        Add(nameof(TestEngineRegressionsAsync), () => Wait(TestEngineRegressionsAsync));
        Add(nameof(TestProcessesAndUpdatesAsync), () => Wait(TestProcessesAndUpdatesAsync));
        Add(nameof(TestDownloadRegressionsAsync), () => Wait(TestDownloadRegressionsAsync));
        Add(nameof(TestAutostartStaysInTray), TestAutostartStaysInTray);
        Add(nameof(TestAutostartToggle), TestAutostartToggle);
        Add(nameof(TestPowerAccessibilityAndSessionEnd), TestPowerAccessibilityAndSessionEnd);
        Add(nameof(TestIconResources), TestIconResources);
        foreach (int limit in new[] { 0, 749, 600 })
        {
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
            foreach (int adjustment in new[] { 0, -100, 7 })
                Add(FormattableString.Invariant($"TestLogLayout[limit={limit},scale={scale},adjustment={adjustment}]"),
                    () => TestLogLayout(limit, scale, adjustment));
            foreach (float scale in new[] { 1.25f, 2f })
                Add(FormattableString.Invariant($"TestLogScalingWhileExpanded[limit={limit},scale={scale}]"),
                    () => TestLogScalingWhileExpanded(limit, scale));
        }
        Add(nameof(CaptureControls), CaptureControls);
        return tests;
    }

    static int RunTests(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            string filter = null, child = null, artifacts = null;
            string results = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results");
            bool list = false, fixtures = false;
            for (int i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--list": list = true; break;
                    case "--runner-fixtures": fixtures = true; break;
                    case "--filter": filter = args[++i]; break;
                    case "--results": results = args[++i]; break;
                    case "--run-test": child = args[++i]; break;
                    case "--artifacts": artifacts = args[++i]; break;
                    default: throw new ArgumentException("Unknown test runner argument: " + args[i]);
                }
            var tests = fixtures ? RunnerFixtures() : TestCases();
            if (child != null)
                return RunChild(tests.Single(t => t.Name == child), Path.GetFullPath(artifacts));
            if (filter != null)
                tests = tests.Where(t => t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (tests.Count == 0) throw new ArgumentException("No tests match filter: " + filter);
            if (list) { foreach (var test in tests) Console.WriteLine(test.Name); return 0; }
            var report = RunSuite(tests, Path.GetFullPath(results), fixtures, !fixtures);
            return report.Any(r => r.Status == "FAIL") ? 1 : 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 2; }
    }

    static int RunChild(TestCase test, string artifacts)
    {
        _artifacts = artifacts;
        Directory.CreateDirectory(_artifacts);
        string status = "PASS", details = "", file = "";
        int line = 0;
        try
        {
            InitializeTest();
            Console.WriteLine(EnvironmentDescription());
            Console.WriteLine("Test data: " + _data);
            test.Body();
        }
        catch (TestSkippedException e) { status = "SKIP"; details = e.Message; }
        catch (Exception e)
        {
            status = "FAIL"; details = e.ToString();
            if (e.GetBaseException() is TestAssertionException assertion)
            { file = SourcePath(assertion.File); line = assertion.Line; }
            Console.Error.WriteLine(details);
        }
        new XElement("result", new XAttribute("name", test.Name), new XAttribute("status", status),
            new XAttribute("checks", _passed), new XAttribute("file", file), new XAttribute("line", line),
            new XElement("details", XmlText(details))).Save(Path.Combine(_artifacts, "result.xml"));
        // Every case unpacks its own copy of the embedded programs (sing-box alone is 45 MB, some cases have two copies), so a green
        // case removes its data folder. A failed case keeps it for inspection: the path is printed at the start of the output.
        if (status != "FAIL" && _data != null) DeleteFolder(_data);
        return status == "FAIL" ? 1 : 0;
    }

    /// <summary>
    /// Best effort: a program that was stopped a moment ago may still hold its exe for a few milliseconds (more on a busy machine, or
    /// while a virus scanner looks at it), so a refused delete is retried a few times.
    /// </summary>
    static void DeleteFolder(string path)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(250);
        }
    }

    /// <summary>The data folders of failed cases stay for inspection; after a day nobody looks at them, and they are large.</summary>
    static void RemoveOldTestData()
    {
        try
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data");
            if (!Directory.Exists(root)) return;
            foreach (var folder in Directory.GetDirectories(root))
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) > TimeSpan.FromDays(1))
                    try { Directory.Delete(folder, true); } catch { }
        }
        catch { }
    }

    static List<CaseResult> RunSuite(List<TestCase> tests, string directory, bool fixtures, bool annotations)
    {
        RemoveOldTestData();
        Directory.CreateDirectory(directory);
        string run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string runDirectory = Path.Combine(directory, run);
        Directory.CreateDirectory(runDirectory);
        File.WriteAllText(Path.Combine(runDirectory, "environment.txt"), EnvironmentDescription(), new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(directory, "selected-tests.txt"), tests.Select(t => t.Name), new UTF8Encoding(false));
        var results = new List<CaseResult>();
        WriteReports(directory, results, tests.Count);
        Console.WriteLine($"Running {tests.Count} independent test cases. Reports: {directory}");
        for (int i = 0; i < tests.Count; i++)
        {
            var test = tests[i];
            string slug = Regex.Replace(test.Name, "[^a-zA-Z0-9_.-]", "_");
            if (slug.Length > 70) slug = slug.Substring(0, 70);
            string caseDirectory = Path.Combine(runDirectory, (i + 1).ToString("D3") + "-" + slug);
            Directory.CreateDirectory(caseDirectory);
            File.WriteAllText(Path.Combine(directory, "current-test.txt"), test.Name + "\nArtifacts: " + caseDirectory + "\nTimeout: " + test.TimeoutMs + " ms", new UTF8Encoding(false));
            Console.WriteLine($"[RUN  {i + 1}/{tests.Count}] {test.Name}");
            var psi = new ProcessStartInfo(typeof(Program).Assembly.Location,
                (fixtures ? "--runner-fixtures " : "") + "--run-test " + QuoteArgument(test.Name) + " --artifacts " + QuoteArgument(caseDirectory));
            var process = RunTestProcess(psi, test.TimeoutMs);
            var result = new CaseResult
            {
                Name = test.Name, Status = "FAIL", Directory = caseDirectory,
                Seconds = process.ElapsedMs / 1000.0, Stdout = process.Stdout, Stderr = process.Stderr
            };
            File.WriteAllText(Path.Combine(caseDirectory, "stdout.txt"), process.Stdout, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(caseDirectory, "stderr.txt"), process.Stderr, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(caseDirectory, "process.txt"), process.Describe(), new UTF8Encoding(false));
            string childReport = Path.Combine(caseDirectory, "result.xml");
            try
            {
                if (process.TimedOut || process.StartError != null || !process.OutputComplete || !File.Exists(childReport))
                    throw new Exception("Test process did not produce a complete result.\n" + process.Describe());
                var element = XElement.Load(childReport);
                if ((string)element.Attribute("name") != test.Name) throw new Exception("Test result name does not match the running case.");
                result.Status = (string)element.Attribute("status");
                result.Checks = (int)element.Attribute("checks");
                result.File = (string)element.Attribute("file");
                result.Line = (int)element.Attribute("line");
                result.Details = (string)element.Element("details");
                if (!(new[] { "PASS", "FAIL", "SKIP" }).Contains(result.Status) ||
                    process.Code != (result.Status == "FAIL" ? 1 : 0))
                    throw new Exception("Test status and process exit code disagree.\n" + process.Describe());
            }
            catch (Exception e) { result.Status = "FAIL"; result.Details = e.ToString(); }
            results.Add(result);
            Console.WriteLine($"[{result.Status}] {test.Name} ({result.Seconds:F2}s, {result.Checks} checks)");
            if (result.Status == "PASS" && result.Stdout.Contains(LaunchRetryMarker))
            {
                const string note = "A process (winws2 or sing-box) was killed by the Windows loader (0xC0000142) and started again; the case passed on a later launch. The case output names it, and winws2.txt in the case directory keeps the winws2 launches.";
                Console.WriteLine(annotations && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
                    ? "::warning title=" + Annotation(test.Name, true) + "::" + note
                    : "Note: " + note);
            }
            if (result.Status != "PASS")
            {
                Console.WriteLine(result.Details);
                Console.WriteLine("Diagnostics: " + caseDirectory);
            }
            if (annotations && result.Status == "FAIL" && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                Console.WriteLine("::error title=" + Annotation(test.Name, true) +
                    (result.File.Length == 0 ? "" : ",file=" + Annotation(result.File, true) + ",line=" + result.Line) +
                    "::" + Annotation(result.Details, false));
            // Keep a usable report even if a later case or the whole job is interrupted.
            WriteReports(directory, results, tests.Count);
        }
        Console.WriteLine($"TOTAL: {results.Count(r => r.Status == "PASS")} passed, {results.Count(r => r.Status == "FAIL")} failed, {results.Count(r => r.Status == "SKIP")} skipped; {results.Sum(r => r.Checks)} checks.");
        foreach (var failure in results.Where(r => r.Status == "FAIL")) Console.WriteLine("FAILED: " + failure.Name);
        Console.WriteLine("WARP/WinDivert were not started. Reports: " + directory);
        File.WriteAllText(Path.Combine(directory, "current-test.txt"), "Finished.", new UTF8Encoding(false));
        return results;
    }

    static string EnvironmentDescription() =>
        $"OS: {Environment.OSVersion}\nCLR: {Environment.Version}\n64-bit process: {Environment.Is64BitProcess}\n" +
        $"Runner image: {Environment.GetEnvironmentVariable("ImageOS")} {Environment.GetEnvironmentVariable("ImageVersion")}\n" +
        $"Culture: {CultureInfo.CurrentCulture.Name}\nScreen: {SystemInformation.PrimaryMonitorSize}\nMaxWindowTrackSize: {SystemInformation.MaxWindowTrackSize}\n";

    static string XmlText(string value) => new string((value ?? "").Where(XmlConvert.IsXmlChar).ToArray());
    static string SourcePath(string file)
    {
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? file.Substring(root.Length) : file).Replace('\\', '/');
    }
    static string Annotation(string value, bool property)
    {
        string escaped = (value ?? "").Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
        return property ? escaped.Replace(":", "%3A").Replace(",", "%2C") : escaped;
    }
    static string Markdown(string value) => (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "&#124;").Replace("`", "&#96;").Replace("\r", "").Replace("\n", "<br>");

    static void WriteReports(string directory, List<CaseResult> results, int total)
    {
        int failed = results.Count(r => r.Status == "FAIL"), skipped = results.Count(r => r.Status == "SKIP");
        var suite = new XElement("testsuite", new XAttribute("name", "Zarp"), new XAttribute("tests", results.Count),
            new XAttribute("failures", failed), new XAttribute("skipped", skipped), new XAttribute("time", results.Sum(r => r.Seconds)));
        foreach (var result in results)
        {
            var element = new XElement("testcase", new XAttribute("name", result.Name), new XAttribute("classname", "Zarp.Tests"), new XAttribute("time", result.Seconds));
            if (result.Status == "FAIL") element.Add(new XElement("failure", new XAttribute("message", XmlText(result.Details.Split('\n')[0])), XmlText(result.Details)));
            if (result.Status == "SKIP") element.Add(new XElement("skipped", new XAttribute("message", XmlText(result.Details))));
            element.Add(new XElement("system-out", XmlText(result.Stdout)), new XElement("system-err", XmlText(result.Stderr)));
            suite.Add(element);
        }
        WriteReportSnapshot(Path.Combine(directory, "results.xml"), new XDocument(suite).ToString());
        var summary = new StringBuilder("## Zarp regression tests\n\n");
        summary.AppendLine($"**{results.Count - failed - skipped} passed, {failed} failed, {skipped} skipped, {total - results.Count} not completed** ({results.Sum(r => r.Checks)} checks).\n");
        foreach (var result in results.Where(r => r.Status != "PASS"))
        {
            summary.AppendLine("### " + result.Status + ": " + Markdown(result.Name) + "\n");
            summary.AppendLine("<pre>" + Markdown(result.Details) + "</pre>\n");
        }
        summary.AppendLine("| Test | Result | Seconds |\n| --- | --- | ---: |");
        foreach (var result in results)
            summary.AppendLine($"| {Markdown(result.Name)} | {result.Status} | {result.Seconds.ToString("F2", CultureInfo.InvariantCulture)} |");
        summary.AppendLine("\nFull stdout, stderr, process exit codes and failing configurations are in the zarp-test-results artifact.");
        WriteReportSnapshot(Path.Combine(directory, "summary.md"), summary.ToString());
    }

    static void WriteReportSnapshot(string path, string content)
    {
        // Preserve the last complete report if the job is interrupted while writing the next one.
        string temporary = path + ".tmp";
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return;
            }
            // A virus scanner or an indexer may hold a freshly written report for a moment; losing the whole run to that is worse than waiting.
            catch (IOException) when (attempt < 8) { Thread.Sleep(100 * attempt); }
            catch (UnauthorizedAccessException) when (attempt < 8) { Thread.Sleep(100 * attempt); }
        }
    }

    static string QuoteArgument(string value)
    {
        // CommandLineToArgvW rules, including trailing backslashes in a quoted path.
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    sealed class TestProcessResult
    {
        public int Code;
        public long ElapsedMs;
        public bool TimedOut, OutputComplete;
        public string Stdout = "", Stderr = "", StartError, Command, WorkingDirectory;
        public string Describe() => $"Command: {Command}\nWorking directory: {WorkingDirectory}\n" +
            $"Exit code: {Code} (0x{unchecked((uint)Code):X8})\nElapsed: {ElapsedMs} ms\nTimed out: {TimedOut}\nOutput complete: {OutputComplete}\n" +
            (StartError == null ? "" : "Start error: " + StartError + "\n") +
            "stdout:\n" + (Stdout.Length == 0 ? "<empty>\n" : Stdout) + "stderr:\n" + (Stderr.Length == 0 ? "<empty>\n" : Stderr);
    }

    static TestProcessResult RunTestProcess(ProcessStartInfo psi, int timeoutMs)
    {
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = new UTF8Encoding(false);
        var result = new TestProcessResult { Command = QuoteArgument(psi.FileName) + " " + psi.Arguments, WorkingDirectory = string.IsNullOrEmpty(psi.WorkingDirectory) ? Environment.CurrentDirectory : psi.WorkingDirectory };
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        var outputDone = new TaskCompletionSource<bool>(); var errorDone = new TaskCompletionSource<bool>();
        var clock = Stopwatch.StartNew();
        bool started = false;
        using (var process = new Process { StartInfo = psi })
        {
            process.OutputDataReceived += (s, e) => { if (e.Data == null) outputDone.TrySetResult(true); else lock (stdout) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data == null) errorDone.TrySetResult(true); else lock (stderr) stderr.AppendLine(e.Data); };
            try
            {
                process.Start(); started = true;
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMs))
                {
                    result.TimedOut = true;
                    // Only terminate this test's process tree, never processes matched by name.
                    try
                    {
                        using (var kill = Process.Start(new ProcessStartInfo(ProcessUtil.SystemExe("taskkill.exe"), "/PID " + process.Id + " /T /F")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                        {
                            kill.BeginOutputReadLine(); kill.BeginErrorReadLine();
                            if (!kill.WaitForExit(5000)) kill.Kill();
                        }
                    }
                    catch (System.ComponentModel.Win32Exception e) { lock (stderr) stderr.AppendLine("taskkill: " + e.Message); }
                    if (!process.HasExited) process.Kill();
                    process.WaitForExit(5000);
                }
                result.Code = process.HasExited ? process.ExitCode : -1;
                result.OutputComplete = Task.WaitAll(new Task[] { outputDone.Task, errorDone.Task }, 5000);
            }
            catch (Exception e) { result.StartError = e.ToString(); result.Code = -1; }
            finally
            {
                if (started)
                {
                    try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception e) { lock (stderr) stderr.AppendLine("Process cleanup: " + e.Message); }
                }
            }
            lock (stdout) result.Stdout = stdout.ToString();
            lock (stderr) result.Stderr = stderr.ToString();
        }
        result.ElapsedMs = clock.ElapsedMilliseconds;
        return result;
    }

    const int LaunchAttempts = 4;
    // Written by DryRun when winws2 had to be started again; RunSuite turns it into a warning for a passing case.
    const string LaunchRetryMarker = "Launch retried:";

    /// <summary>
    /// STATUS_DLL_INIT_FAILED with no output and no timeout: the Windows loader killed the process before any of its code ran,
    /// so the result says nothing about what it was asked to do. A freshly extracted Cygwin binary now and then dies like this
    /// on a busy CI runner, whatever its arguments are.
    /// </summary>
    static bool DiedInLoader(TestProcessResult run) =>
        run.StartError == null && !run.TimedOut && run.OutputComplete && run.Code == unchecked((int)0xC0000142) &&
        run.Stdout.Length == 0 && run.Stderr.Length == 0;

    /// <summary>
    /// Starts the process again only when it died in the loader; any other result, good or bad, is final. The launches that died
    /// are added to lost so that the diagnostics keep them. After LaunchAttempts launches the last result is returned as it is.
    /// </summary>
    static TestProcessResult RunUntilLoaded(Func<TestProcessResult> launch, List<TestProcessResult> lost, int delayMs = 500)
    {
        var run = launch();
        while (DiedInLoader(run) && lost.Count < LaunchAttempts - 1)
        {
            lost.Add(run);
            Thread.Sleep(delayMs * lost.Count);
            run = launch();
        }
        return run;
    }

    static List<TestCase> RunnerFixtures() => new List<TestCase>
    {
        new TestCase { Name = "ExpectedFailureOne", Body = () => Check(false, "first failure <tag> & detail\nnext line") },
        new TestCase { Name = "ExpectedFailureTwo", Body = () => Check(false, "second failure") },
        new TestCase { Name = "ExpectedCrash", Body = () => Environment.Exit(37) },
        new TestCase { Name = "ExpectedTimeout", TimeoutMs = 3000, Body = () => { Console.WriteLine("before timeout"); Thread.Sleep(Timeout.Infinite); } },
        new TestCase { Name = "ExpectedSkip", Body = () => { throw new TestSkippedException("deliberate skip"); } },
        new TestCase { Name = "PassAfterFailures", Body = () => { Console.WriteLine("stdout tail"); Console.Error.WriteLine("stderr tail"); Check(true, "still runs"); } }
    };

    static void TestRunnerReporting()
    {
        string directory = Path.Combine(_artifacts, "expected failures with spaces");
        var results = RunSuite(RunnerFixtures(), directory, true, false);
        Check(results.Count == 6 && results.Count(r => r.Status == "FAIL") == 4, "Collect every independent assertion failure, crash and timeout");
        Check(results.Last().Status == "PASS" && results.Last().Checks == 1, "A passing test after failures must still run");
        Check(results.Last().Stdout.Contains("stdout tail") && results.Last().Stderr.Contains("stderr tail"), "Drain both output streams before reporting completion");
        Check(results[2].Details.Contains("0x00000025") && results[3].Details.Contains("Timed out: True"), "Crashes and timeouts must have explicit process diagnostics");
        Check(results[3].Stdout.Contains("before timeout"), "Keep partial output when a test times out");
        Check(results[4].Status == "SKIP", "A skipped test must not be reported as a pass");
        Check(results[0].File.EndsWith("tests/Zarp.Tests/TestRunner.cs") && results[0].Line > 0, "Assertions report the source file and line for annotations");
        var xml = XDocument.Load(Path.Combine(directory, "results.xml"));
        Check(xml.Descendants("failure").Count() == 4 && xml.Descendants("testcase").Count() == 6, "JUnit includes all cases and failures");
        Check(xml.Descendants("failure").First().Value.Contains("<tag> & detail"), "JUnit preserves special characters in errors");
        string summary = File.ReadAllText(Path.Combine(directory, "summary.md"));
        Check(summary.Contains("ExpectedFailureOne") && summary.Contains("ExpectedFailureTwo") && summary.Contains("PassAfterFailures"), "Summary lists multiple failures and later passes");
        Check(Annotation("a,b\r\n%c", true) == "a%2Cb%0D%0A%25c", "Workflow annotations escape properties and multiline messages");
        var missing = RunTestProcess(new ProcessStartInfo(Path.Combine(_data, "missing-test-executable.exe")), 1000);
        Check(missing.StartError != null && missing.Code != 0, "A process that cannot start must retain its launch error");
    }
}
