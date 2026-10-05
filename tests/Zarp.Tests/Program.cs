using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zarp.Core;

static class Program
{
    const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static readonly Assembly AppAssembly = typeof(Engine).Assembly;
    static string _data;
    static int _passed;

    [STAThread]
    static int Main()
    {
        _data = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_data);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL: checks did not finish within 120 seconds.");
            Environment.Exit(1);
        }, null, 120000, System.Threading.Timeout.Infinite);

        // Isolate UI tests from embedded driver extraction, WARP, and background downloads.
        typeof(Zapret).GetField("_embeddedVersion", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, "");
        // Tests must never find, start or install the real Cloudflare WARP: the search always comes back empty
        // (individual tests substitute their own results).
        typeof(Warp).GetField("Finder", PrivateStatic).SetValue(null, (Func<WarpLocator.Result>)Missing);
        try
        {
            TestConfig();
            TestVpnDetection();
            TestDialog();
            TestClose(cancel: true, remember: true, tray: true);
            TestClose(cancel: false, remember: false, tray: true);
            TestClose(cancel: false, remember: true, tray: true);
            TestClose(cancel: false, remember: false, tray: false);
            TestClose(cancel: false, remember: true, tray: false);
            TestSavedAction(tray: true);
            TestSavedAction(tray: false);
            TestExplicitExit();
            TestExitDuringPrompt();
            TestShutdownDuringOperation();
            TestSettings();
            TestLocalization();
            TestKeysUsedInCode();
            TestWarpLocator();
            TestWarpInstaller();
            TestWarpDownload();
            TestEnsureWarp();
            TestInstallHint();
            CaptureControls();
            Console.WriteLine("PASS: " + _passed + " checks; WARP/WinDivert were not started.");
            watchdog.Dispose();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _passed++;
    }

    static void TestConfig()
    {
        string path = Path.Combine(_data, "config.json");
        var config = AppConfig.Load(path);
        Check(config.AskBeforeClose, "New installs must ask before closing");
        File.WriteAllText(path, "{\"MinimizeToTray\":false,\"TestTimeoutSec\":27}");
        config = AppConfig.Load(path);
        Check(config.AskBeforeClose && !config.MinimizeToTray && config.TestTimeoutSec == 27,
            "Existing settings must survive the upgrade and receive the new prompt");
        config.AskBeforeClose = false;
        config.Save();
        config = AppConfig.Load(path);
        Check(!config.AskBeforeClose && !config.MinimizeToTray, "Remembered exit must survive restart");
        config.MinimizeToTray = true;
        config.Save();
        Check(AppConfig.Load(path).MinimizeToTray, "Remembered tray action must survive restart");
    }

    static void TestDialog()
    {
        using (var prompt = NewForm("CloseActionForm", true, true))
        {
            PositionOffscreen(prompt);
            prompt.Show();
            Application.DoEvents();
            Check(prompt.Controls.OfType<Button>().Count() == 2, "Both close actions must be visible");
            Check(prompt.Controls.OfType<CheckBox>().Single().Checked == false, "Remember must be opt-in");
            foreach (Control control in prompt.Controls)
                Check(prompt.ClientRectangle.Contains(control.Bounds), "Dialog control is clipped: " + control.Text);
            SaveImage(prompt, "close-dialog.png");
            var handled = (bool)prompt.GetType().GetMethod("ProcessDialogKey", PrivateInstance)
                .Invoke(prompt, new object[] { Keys.Escape });
            Check(handled && prompt.DialogResult == DialogResult.Cancel, "Escape must cancel the close prompt");
        }
    }

    static void TestVpnDetection()
    {
        var detect = typeof(NetCheck).GetMethod("IsForeignVpnAdapter", BindingFlags.NonPublic | BindingFlags.Static);
        bool IsVpn(string name, string description, NetworkInterfaceType type = NetworkInterfaceType.Tunnel,
            OperationalStatus status = OperationalStatus.Up, params string[] gateways) =>
            (bool)detect.Invoke(null, new object[] { name, description, type, status, gateways.Select(IPAddress.Parse).ToArray() });

        foreach (var name in new[] { "Teredo Tunneling Pseudo-Interface", "Microsoft ISATAP Adapter", "Microsoft 6to4 Adapter" })
            Check(!IsVpn(name, "", NetworkInterfaceType.Tunnel, OperationalStatus.Up, "192.168.0.1"),
                "Windows IPv6 tunnel must not be reported as VPN: " + name);
        Check(!IsVpn("Custom name", "Microsoft Teredo Tunneling Adapter", NetworkInterfaceType.Tunnel, OperationalStatus.Up, "fe80::1"),
            "Renaming Teredo must not cause a VPN warning");
        Check(!IsVpn("CloudflareWARP", "Cloudflare WARP", NetworkInterfaceType.Tunnel, OperationalStatus.Up, "172.16.0.1"),
            "WARP itself must be excluded");
        Check(!IsVpn("happ-tun", "sing-tun", NetworkInterfaceType.Tunnel, OperationalStatus.Down, "172.18.0.2"),
            "Disabled VPN must be excluded");
        Check(!IsVpn("happ-tun", "sing-tun", NetworkInterfaceType.Tunnel, OperationalStatus.Up, "0.0.0.0", "::"),
            "Unspecified IPv4/IPv6 addresses must not count as gateways");
        Check(!IsVpn("happ-tun", "sing-tun"), "VPN without gateway must not be reported");
        Check(IsVpn("happ-tun", "sing-tun", NetworkInterfaceType.Tunnel, OperationalStatus.Up, "172.18.0.2"),
            "Active VPN must still be detected");
        Check(IsVpn("tun0", "", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "10.0.0.1"),
            "Numbered TUN adapters must still be detected");
        Check(IsVpn("Ethernet 2", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "10.0.0.1"),
            "TAP adapters must still be detected");
        Check(IsVpn("WireGuard", "", NetworkInterfaceType.Tunnel, OperationalStatus.Up, "fe80::1"),
            "Real IPv6 gateways must still be detected");
        Check(IsVpn("Microsoft IP-HTTPS Platform Interface", "", NetworkInterfaceType.Tunnel, OperationalStatus.Up, "fe80::1"),
            "DirectAccess tunnels must not be excluded with ordinary IPv6 transition adapters");
        Check(!IsVpn("Tuning Ethernet", "Realtek", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "192.168.0.1"),
            "The tun substring in an ordinary adapter name must not imply VPN");
    }

    static Engine NewEngine()
    {
        string dir = Path.Combine(_data, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var engine = new Engine(dir);
        typeof(Warp).GetField("<CliPath>k__BackingField", PrivateInstance).SetValue(engine.Warp, null);
        engine.Config.AutoUpdateZapret = false;
        // Exercise application exit without changing the user's network or stopping processes.
        engine.Config.DisconnectOnExit = false;
        return engine;
    }

    static Form NewForm(string name, params object[] args) =>
        (Form)Activator.CreateInstance(AppAssembly.GetType("Zarp.UI." + name), args);

    static Form NewMain(Engine engine)
    {
        var main = NewForm("MainForm", engine, false, false);
        PositionOffscreen(main);
        // Balloon tips would otherwise be displayed on the developer's desktop.
        main.GetType().GetField("_trayHintShown", PrivateInstance).SetValue(main, true);
        main.Show();
        Application.DoEvents();
        return main;
    }

    static void PositionOffscreen(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
    }

    static void TestClose(bool cancel, bool remember, bool tray)
    {
        var engine = NewEngine();
        using (var main = NewMain(engine))
        using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
        {
            bool prompted = false;
            Exception failure = null;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var prompt = Application.OpenForms.Cast<Form>().SingleOrDefault(f => f.GetType().Name == "CloseActionForm");
                try
                {
                    Check(prompt != null, "Closing must display a choice");
                    prompted = true;
                    main.Close();
                    Check(Application.OpenForms.Cast<Form>().Count(f => f.GetType().Name == "CloseActionForm") == 1,
                        "Repeated close must not open another dialog");
                    prompt.Controls.OfType<CheckBox>().Single().Checked = remember;
                    if (cancel) prompt.Close();
                    else prompt.Controls.OfType<Button>().Single(b => b.Name == (tray ? "tray" : "exit")).PerformClick();
                }
                catch (Exception ex)
                {
                    failure = ex;
                    if (prompt != null) prompt.DialogResult = DialogResult.Cancel;
                }
            };
            timer.Start();
            main.Close();
            Application.DoEvents();
            if (failure != null) throw failure;
            Check(prompted, "Prompt callback must run");
            Check(main.Visible == cancel, "Cancel keeps the window; a chosen action hides it");
            Check(main.IsDisposed == (!cancel && !tray), "Only explicit exit disposes the application window");
            Check(engine.Config.AskBeforeClose == (cancel || !remember), "Only a confirmed remember choice disables prompting");
            if (!cancel && remember)
            {
                var saved = AppConfig.Load(Path.Combine(engine.DataDir, "zarp.json"));
                Check(!saved.AskBeforeClose && saved.MinimizeToTray == tray, "Selected action must be persisted");
            }
            else
                Check(!File.Exists(Path.Combine(engine.DataDir, "zarp.json")), "Cancel/one-off choice must not write preferences");
        }
    }

    static void TestSavedAction(bool tray)
    {
        var engine = NewEngine();
        engine.Config.AskBeforeClose = false;
        engine.Config.MinimizeToTray = tray;
        using (var main = NewMain(engine))
        {
            main.Close();
            Application.DoEvents();
            Check(!main.Visible && main.IsDisposed == !tray, "Saved action must run without another prompt");
        }
    }

    static void TestExplicitExit()
    {
        var engine = NewEngine();
        using (var main = NewMain(engine))
        {
            // Tray Exit must bypass the close preference even when prompting is enabled.
            main.GetType().GetMethod("ExitApp", PrivateInstance).Invoke(main, null);
            Application.DoEvents();
            Check(main.IsDisposed, "Tray Exit must quit without a close dialog");
        }
    }

    static void TestSettings()
    {
        var engine = NewEngine();
        using (var settings = NewForm("SettingsForm", engine))
        {
            PositionOffscreen(settings);
            settings.Show();
            // Do not pump messages yet: the async autostart query is still pending.
            var choice = (Button)settings.GetType().GetField("_closeAction", PrivateInstance).GetValue(settings);
            void Select(int index) => choice.GetType().GetProperty("SelectedIndex").SetValue(choice, index);
            Select(2);
            Check(!engine.Config.AskBeforeClose && !engine.Config.MinimizeToTray,
                "Exit preference must save while the autostart query is pending");
            Select(1);
            Check(!engine.Config.AskBeforeClose && engine.Config.MinimizeToTray, "Settings must support tray by default");
            Select(0);
            Check(engine.Config.AskBeforeClose, "Settings must restore prompting");
            Application.DoEvents();
            Check(choice.Parent.ClientRectangle.Contains(choice.Bounds), "Close dropdown must fit in its row");
            foreach (Control control in choice.Parent.Parent.Controls)
                Check(choice.Parent.Parent.ClientRectangle.Contains(control.Bounds), "Settings option is clipped: " + control.Text);
            SaveImage(settings, "settings.png");
            choice.GetType().GetMethod("OnKeyDown", PrivateInstance).Invoke(choice, new object[] { new KeyEventArgs(Keys.Down) });
            Check(!engine.Config.AskBeforeClose && engine.Config.MinimizeToTray, "Keyboard must change and save the selection");
            choice.PerformClick();
            var menu = (ContextMenuStrip)choice.GetType().GetField("_menu", PrivateInstance).GetValue(choice);
            Check(menu.Visible && menu.Items.Count == 3, "All close options must appear in the dropdown");
            menu.Items[2].PerformClick();
            Check(!engine.Config.AskBeforeClose && !engine.Config.MinimizeToTray, "Popup selection must change and save the preference");
            menu.Close();
        }
    }

    static readonly string[] ExpectedLanguages = { "en", "ru", "es", "pt", "zh", "hi", "fr", "de" };

    static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text ?? "", @"\{\d+\}").Cast<Match>().Select(m => m.Value).Distinct().OrderBy(v => v));

    static void TestLocalization()
    {
        var codes = L.Languages.Select(l => l.Code).ToArray();
        Check(codes.SequenceEqual(ExpectedLanguages), "All eight languages must be offered in the menu order");
        var english = new HashSet<string>(L.KeysOf("en"));
        Check(english.Count > 100, "English strings must load from the embedded resource");
        foreach (var code in codes)
        {
            var keys = new HashSet<string>(L.KeysOf(code));
            var missing = english.Except(keys).ToList();
            var extra = keys.Except(english).ToList();
            Check(missing.Count == 0 && extra.Count == 0,
                code + ": missing [" + string.Join(", ", missing) + "], extra [" + string.Join(", ", extra) + "]");
            foreach (var key in english)
                Check(Placeholders(L.Raw("en", key)) == Placeholders(L.Raw(code, key)), code + "." + key + ": placeholders differ from English");
        }

        // язык Windows: региональные варианты сводятся к языку, неподдерживаемый - к английскому
        foreach (var pair in new[] { ("pt-BR", "pt"), ("pt-PT", "pt"), ("zh-CN", "zh"), ("hi-IN", "hi"), ("de-AT", "de"),
                                     ("fr-CA", "fr"), ("es-MX", "es"), ("ru-RU", "ru"), ("en-GB", "en"), ("ja-JP", "en"), ("uk-UA", "en") })
            Check(L.Map(new CultureInfo(pair.Item1)) == pair.Item2, pair.Item1 + " must map to " + pair.Item2);
        Check(L.Map(CultureInfo.InvariantCulture) == "en", "Invariant culture must fall back to English");
        Check(L.Resolve(null) == L.SystemLanguage && L.Resolve("") == L.SystemLanguage && L.Resolve("xx") == L.SystemLanguage,
            "Empty or unknown setting must follow the system language");
        Check(L.Resolve("hi") == "hi", "Explicit choice must win over the system language");

        // выбор языка переживает перезапуск и читается до создания окон
        string path = Path.Combine(_data, "language.json");
        var config = AppConfig.Load(path);
        Check(config.Language == null && AppConfig.ReadLanguage(path) == null, "New installs must follow the system language");
        config.Language = "zh";
        config.Save();
        Check(AppConfig.ReadLanguage(path) == "zh" && AppConfig.Load(path).Language == "zh", "Chosen language must survive restart");
        File.WriteAllText(path, "{broken");
        Check(AppConfig.ReadLanguage(path) == null, "Broken settings must not stop the app from starting");

        try
        {
            // сохранённые результаты проверок показываются на текущем языке
            var result = new TestResult { StrategyId = "x" };
            result.Fail(new Msg("err.timeout", 15));
            string resultsPath = Path.Combine(_data, "results.json");
            var withResult = AppConfig.Load(resultsPath);
            withResult.Results["x"] = result;
            withResult.Save();
            var restored = AppConfig.Load(resultsPath).Results["x"];
            L.Apply("ru");
            Check(restored.DisplayError == "нет подключения за 15 с", "Saved error must be shown in Russian: " + restored.DisplayError);
            restored.Rechecked = true;
            Check(restored.DisplayError == "не подтвердилась: нет подключения за 15 с", "Re-check failure must be prefixed");
            L.Apply("de");
            Check(restored.DisplayError == "nicht bestätigt: keine Verbindung innerhalb von 15 s", "Saved error must follow the language");
            var direct = StrategyCatalog.Load(NewEngine().DataDir).Single(s => s.Id == "direct");
            Check(direct.Name == "Ohne zapret (direkte Verbindung)", "Built-in strategy name must be translated");

            foreach (var code in codes)
            {
                L.Apply(code);
                Check(L.Current == code, code + ": language must switch");
                var engine = NewEngine();
                using (var settings = NewForm("SettingsForm", engine))
                {
                    PositionOffscreen(settings);
                    settings.Show();
                    Application.DoEvents();
                    CheckScanButtons(settings, busy: false, code);
                    CheckLayout(settings, code);
                    Control ByText(string key) => settings.Controls.Cast<Control>().Single(c => c.Text == L.T(key));
                    int edge = ByText("btn.close").Right;
                    Check(ByText("btn.defender").Right == edge && ByText("btn.checkUpdates").Right == edge,
                        code + ": right column buttons must line up with Close");
                    SaveImage(settings, "settings-" + code + ".png");

                    // во время поиска «Отмена» встаёт на место кнопок поиска - ряд тоже должен влезать
                    var state = typeof(Engine).GetProperty("State");
                    var update = settings.GetType().GetMethod("UpdateButtons", PrivateInstance);
                    state.SetValue(engine, EngineState.Searching);
                    update.Invoke(settings, null);
                    Application.DoEvents();
                    CheckScanButtons(settings, busy: true, code);
                    CheckLayout(settings, code + " (searching)");
                    state.SetValue(engine, EngineState.Idle);
                    update.Invoke(settings, null);
                }
                using (var main = NewMain(engine))
                {
                    CheckLayout(main, code);
                    SaveImage(main, "main-" + code + ".png");
                }
                foreach (bool tray in new[] { true, false })
                using (var prompt = NewForm("CloseActionForm", tray, tray))
                {
                    PositionOffscreen(prompt);
                    prompt.Show();
                    Application.DoEvents();
                    CheckLayout(prompt, code);
                    if (tray) SaveImage(prompt, "close-" + code + ".png");
                }
            }

            // открытое окно меняет язык сразу, без перезапуска
            L.Apply("en");
            using (var main = NewMain(NewEngine()))
            {
                var status = (Label)main.GetType().GetField("_status", PrivateInstance).GetValue(main);
                var sub = (Label)main.GetType().GetField("_sub", PrivateInstance).GetValue(main);
                Check(status.Text == "Disconnected", "English status expected before switching");
                L.Apply("fr");
                Check(status.Text == "Déconnecté" && sub.Text == "Cloudflare WARP via zapret2", "Open window must switch language immediately");
            }

            // меню языков: «как в системе» + все языки под собственными названиями; выбор сразу применяется и сохраняется
            L.Apply("en");
            var menuEngine = NewEngine();
            using (var main = NewMain(menuEngine))
            {
                var show = main.GetType().GetMethod("ShowLanguageMenu", PrivateInstance);
                var menu = (ContextMenuStrip)main.GetType().GetField("_languageMenu", PrivateInstance).GetValue(main);
                show.Invoke(main, null);
                Application.DoEvents();
                var items = menu.Items.OfType<ToolStripMenuItem>().ToList();
                Check(menu.Visible && items.Count == 1 + L.Languages.Count, "Language menu must list the system option and every language");
                Check(items[0].Checked && items.Skip(1).Select(i => i.Text).SequenceEqual(L.Languages.Select(l => l.NativeName)),
                    "Languages must be listed by their own names, system option checked by default");
                SaveImage(menu, "language-menu.png");
                items.Single(i => i.Text == "Deutsch").PerformClick();
                string saved = Path.Combine(menuEngine.DataDir, "zarp.json");
                Check(L.Current == "de" && menuEngine.Config.Language == "de" && AppConfig.ReadLanguage(saved) == "de",
                    "Choosing a language must apply and save it");
                if (menu.Visible) menu.Close();
                show.Invoke(main, null);
                items = menu.Items.OfType<ToolStripMenuItem>().ToList();
                Check(items.Single(i => i.Checked).Text == "Deutsch", "The chosen language must be marked in the menu");
                items[0].PerformClick();
                Check(menuEngine.Config.Language == null && AppConfig.ReadLanguage(saved) == null && L.Current == L.SystemLanguage,
                    "The system option must follow Windows again");
                if (menu.Visible) menu.Close();
            }
        }
        finally
        {
            L.Apply("en");
        }
    }

    static void CheckScanButtons(Form settings, bool busy, string code)
    {
        Control Field(string name) => (Control)settings.GetType().GetField(name, PrivateInstance).GetValue(settings);
        var quick = Field("_quick");
        var full = Field("_full");
        var cancel = Field("_cancel");
        Check(quick.Visible == !busy && full.Visible == !busy && cancel.Visible == busy,
            code + ": quick/full scan must show when idle and give way to Cancel while searching");
        Check(quick.Text == L.T("btn.quickScan") && full.Text == L.T("btn.fullScan"), code + ": scan buttons must be translated");
    }

    /// <summary>Ничего не обрезано, текст влезает в кнопки и метки, соседние элементы не налезают друг на друга.</summary>
    static void CheckLayout(Control parent, string code)
    {
        var visible = parent.Controls.Cast<Control>().Where(c => c.Visible).ToList();
        foreach (var c in visible)
        {
            string name = code + ": " + c.GetType().Name + " \"" + c.Text + "\"";
            Check(parent.ClientRectangle.Contains(c.Bounds), name + " is clipped by " + parent.GetType().Name);
            if (c is Button button && button.Text.Length > 0)
            {
                // DarkSelect рисует текст левее стрелки, остальные кнопки - по центру с полями
                int room = button.GetType().Name == "DarkSelect"
                    ? button.Width - button.Height - Math.Max(10, button.Height / 3)
                    : button.Width - 12;
                Check(TextRenderer.MeasureText(button.Text, button.Font).Width <= room, name + " does not fit the button");
            }
            else if (c is Label label && !label.AutoSize && label.Text.Length > 0)
            {
                var need = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak);
                Check(need.Height <= label.Height + 2, name + " does not fit the label");
            }
            else if (c is ListView list)
            {
                foreach (ColumnHeader column in list.Columns)
                    if (column.Text.Length > 0)
                        Check(TextRenderer.MeasureText(column.Text, list.Font).Width + 12 <= column.Width,
                            code + ": column \"" + column.Text + "\" is too narrow");
            }
            if (c is Panel) CheckLayout(c, code); // FlowLayoutPanel тоже Panel
        }
        for (int i = 0; i < visible.Count; i++)
            for (int j = i + 1; j < visible.Count; j++)
                Check(!visible[i].Bounds.IntersectsWith(visible[j].Bounds),
                    code + ": \"" + visible[i].Text + "\" overlaps \"" + visible[j].Text + "\"");
    }

    /// <summary>Каждый ключ из кода есть в переводах, и в переводах нет забытых ключей.</summary>
    static void TestKeysUsedInCode()
    {
        string src = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "src", "Zarp"));
        if (!Directory.Exists(src))
        {
            Console.WriteLine("SKIP: sources not found at " + src + ", key usage was not checked");
            return;
        }
        var pattern = new Regex("\"((?:main|lang|status|hint|tray|dlg|close|settings|col|btn|tip|opt|result|strategy|detail|progress|err|log)\\.[A-Za-z0-9]+)\"");
        var used = new HashSet<string>(Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => pattern.Matches(File.ReadAllText(f)).Cast<Match>().Select(m => m.Groups[1].Value)));
        var defined = new HashSet<string>(L.KeysOf("en"));
        var missing = used.Except(defined).ToList();
        var unused = defined.Except(used).ToList();
        Check(missing.Count == 0, "Keys used in code but missing in en.txt: " + string.Join(", ", missing));
        Check(unused.Count == 0, "Keys in en.txt that the code never uses: " + string.Join(", ", unused));
    }

    // ------------------------------------------------------------------ Cloudflare WARP: поиск и установка

    static WarpLocator.Result Missing()
    {
        var r = new WarpLocator.Result();
        r.Report.Add("service CloudflareWARP: not registered");
        return r;
    }

    static WarpLocator.Result Found(string cli)
    {
        var r = new WarpLocator.Result { CliPath = cli };
        r.Report.Add("found: " + cli);
        return r;
    }

    static void SetFinder(Func<WarpLocator.Result> finder) =>
        typeof(Warp).GetField("Finder", PrivateStatic).SetValue(null, finder);

    static T Sync<T>(Func<Task<T>> body) => Task.Run(body).GetAwaiter().GetResult();

    static Exception Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception e) { return e; }
    }

    static void TestWarpLocator()
    {
        string Parse(string s) => (string)typeof(WarpLocator).GetMethod("ParseImagePath", PrivateStatic).Invoke(null, new object[] { s });
        Check(Parse("\"C:\\Program Files\\Cloudflare\\Cloudflare WARP\\warp-svc.exe\"") == @"C:\Program Files\Cloudflare\Cloudflare WARP\warp-svc.exe",
            "A quoted service path must be unquoted");
        Check(Parse(@"C:\Tools\Cloudflare WARP\warp-svc.exe -k run") == @"C:\Tools\Cloudflare WARP\warp-svc.exe",
            "Arguments after an unquoted service path must be dropped");
        Check(Parse("\"D:\\My Apps\\x.exe\" --flag") == @"D:\My Apps\x.exe", "Quoted path with arguments");
        Check(Parse(@"%SystemRoot%\system32\svchost.exe -k x") == Environment.ExpandEnvironmentVariables(@"%SystemRoot%\system32\svchost.exe"),
            "Environment variables in the service path must be expanded");
        Check(Parse(null) == "" && Parse("   ") == "", "Empty service path must not crash");

        // WARP, установленный на другой диск или в Program Files (x86), больше не остаётся незамеченным
        var folders = ((IEnumerable<string>)typeof(WarpLocator).GetMethod("KnownFolders", PrivateStatic).Invoke(null, null)).ToList();
        Check(folders.Count > 0 && folders.All(f => f.IndexOf("Cloudflare", StringComparison.OrdinalIgnoreCase) > 0), "Candidate folders must be Cloudflare folders");
        Check(folders.Any(f => f.EndsWith(@"Cloudflare\Cloudflare WARP")) && folders.Any(f => f.EndsWith(@"Cloudflare\Cloudflare One Client")),
            "Both product names must be searched");
        Check(folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() == folders.Count, "Candidate folders must not repeat");
        string x86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (x86 != null) Check(folders.Any(f => f.StartsWith(x86, StringComparison.OrdinalIgnoreCase)), "Program Files (x86) must be searched");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            Check(folders.Any(f => f.StartsWith(drive.Name, StringComparison.OrdinalIgnoreCase)), "Drive must be searched: " + drive.Name);

        // настоящий поиск только читает реестр и файлы: не падает и объясняет, что проверял
        var real = WarpLocator.Find();
        Check(real.Report.Count > 0, "The search must report what it checked");
        Check(real.CliPath == null || File.Exists(real.CliPath), "A found warp-cli must exist");

        // Warp запоминает результат и умеет искать заново (WARP могли поставить уже после запуска Zarp)
        SetFinder(Missing);
        var warp = new Warp();
        Check(!warp.Installed && warp.LastSearch.Report.Count == 1, "Missing WARP must be reported");
        SetFinder(() => Found(@"C:\Fake\warp-cli.exe"));
        warp.Locate();
        Check(warp.Installed && warp.CliPath == @"C:\Fake\warp-cli.exe", "Locate must pick up a WARP installed later");
        SetFinder(Missing);
    }

    static void TestWarpInstaller()
    {
        bool Signer(string subject) => (bool)typeof(WarpInstaller).GetMethod("IsCloudflareSigner", PrivateStatic).Invoke(null, new object[] { subject });
        Check(Signer("CN=\"Cloudflare, Inc.\", O=\"Cloudflare, Inc.\", L=San Francisco, S=California, C=US"), "Real Cloudflare certificate subject");
        Check(Signer("CN=x, O=Cloudflare, Inc., C=US"), "Unquoted organization");
        Check(!Signer("CN=Cloudflare, Inc., O=Evil Corp, C=US"), "A Cloudflare name outside O= must not count");
        Check(!Signer("CN=a, O=Not Cloudflare, Inc., C=US"), "A longer organization name must not count");
        Check(!Signer("O=Cloudflare, Inc.x"), "Trailing characters must not count");
        Check(!Signer("CN=Microsoft Windows, O=Microsoft Corporation, C=US") && !Signer("") && !Signer(null), "Other signers and empty subjects");

        var interpret = typeof(WarpInstaller).GetMethod("InterpretMsiExit", PrivateStatic);
        WarpInstaller.MsiResult Exit(int code) => (WarpInstaller.MsiResult)interpret.Invoke(null, new object[] { code });
        Check(Exit(0) == WarpInstaller.MsiResult.Installed, "msiexec 0 is success");
        Check(Exit(3010) == WarpInstaller.MsiResult.RestartNeeded && Exit(1641) == WarpInstaller.MsiResult.RestartNeeded, "Restart codes");
        Check(Exit(1638) == WarpInstaller.MsiResult.AlreadyInstalled, "1638: this or a newer version is already installed");
        foreach (int code in new[] { 1602, 1603, 1618, 1625, 1 })
            Check(Exit(code) == WarpInstaller.MsiResult.Failed, "msiexec " + code + " is a failure");

        // установщик, положенный вручную (если сайт Cloudflare заблокирован)
        string dir = Path.Combine(_data, "manual");
        Directory.CreateDirectory(dir);
        string FindManual() => (string)typeof(WarpInstaller).GetMethod("FindManualInstaller", PrivateStatic).Invoke(null, new object[] { dir });
        Check(FindManual() == null, "No manual installer in an empty folder");
        File.WriteAllText(Path.Combine(dir, "notes.msi"), "x");
        File.WriteAllText(Path.Combine(dir, "Cloudflare_WARP.exe"), "x");
        Check(FindManual() == null, "Only Cloudflare_WARP*.msi counts as a manual installer");
        string old = Path.Combine(dir, "Cloudflare_WARP_2025.msi");
        File.WriteAllText(old, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-5));
        string fresh = Path.Combine(dir, "Cloudflare_WARP.msi");
        File.WriteAllText(fresh, "x");
        Check(FindManual() == fresh, "The newest manual installer must win");

        // решение по результату проверки на готовых данных: принимается только действительная подпись Cloudflare,
        // и результат не зависит от того, какие подписи есть у файлов этой Windows
        var decide = typeof(WarpInstaller).GetMethod("CheckSigner", PrivateStatic);
        string Decide(bool trusted, string problem = null, string subject = null)
        {
            try { return (string)decide.Invoke(null, new object[] { new Authenticode.Result { Trusted = trusted, Problem = problem, SignerSubject = subject } }); }
            catch (TargetInvocationException e)
            {
                // рефлексия оборачивает исключение: достаём настоящее
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
        const string Cloudflare = "CN=\"Cloudflare, Inc.\", O=\"Cloudflare, Inc.\", L=San Francisco, C=US";
        Check(Decide(true, null, Cloudflare) == "Cloudflare, Inc.", "A trusted Cloudflare signature is accepted");
        var noCertificate = Catch(() => Decide(true, null, ""));
        Check(noCertificate != null && noCertificate.Message.Contains("no signer certificate"),
            "A trusted signature without a signer certificate must be rejected: " + noCertificate?.Message);
        var microsoft = Catch(() => Decide(true, null, "CN=Microsoft Windows, O=Microsoft Corporation, C=US"));
        Check(microsoft != null && microsoft.Message.Contains("Microsoft"), "A signature of someone else must be rejected: " + microsoft?.Message);
        foreach (string problem in new[] { "NotSigned", "HashMismatch", "NotTrusted", "Expired", "Revoked" })
        {
            var rejected = Catch(() => Decide(false, problem, Cloudflare)); // даже с подписью Cloudflare в субъекте
            Check(rejected != null && rejected.Message.Contains(problem), problem + " must be rejected: " + rejected?.Message);
        }
        Check(Catch(() => Decide(false)) != null, "A failed check without a reason must still be rejected");

        // коды WinVerifyTrust называются по-человечески
        var describe = typeof(Authenticode).GetMethod("Describe", PrivateStatic);
        string Describe(uint code) => (string)describe.Invoke(null, new object[] { code });
        Check(Describe(0x800B0100) == "NotSigned" && Describe(0x80096010) == "HashMismatch" && Describe(0x800B0109) == "NotTrusted"
              && Describe(0x800B0101) == "Expired" && Describe(0x800B010C) == "Revoked" && Describe(0x80070002) == "FileNotFound",
            "WinVerifyTrust codes must have readable names");
        Check(Describe(0x12345678) == "error 0x12345678", "Unknown codes are shown as hex");

        // и по-настоящему, через WinVerifyTrust: неподписанный файл, как во время установки открытый на чтение
        string unsigned = Assembly.GetExecutingAssembly().Location;
        using (new FileStream(unsigned, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var none = Authenticode.Verify(unsigned);
            Check(!none.Trusted && none.Problem == "NotSigned", "An unsigned file must be reported as NotSigned even while locked for reading: " + none.Problem);
            var refused = Catch(() => Sync(() => (Task<string>)typeof(WarpInstaller).GetMethod("VerifySignatureAsync", PrivateStatic).Invoke(null, new object[] { unsigned })));
            Check(refused != null && refused.Message.Contains("NotSigned"), "The installer check must reject an unsigned file: " + refused?.Message);
        }
        Check(!Authenticode.Verify(Path.Combine(Environment.SystemDirectory, "cmd.exe")).Trusted, "A catalog-signed system file has no embedded signature for Cloudflare");
        var missingFile = Authenticode.Verify(Path.Combine(_data, "does-not-exist.msi"));
        Check(!missingFile.Trusted && missingFile.Problem != null, "A missing file must be rejected: " + missingFile.Problem);
        // файл с настоящей встроенной подписью другого издателя (dotnet.exe от Microsoft): подпись действительна, но не Cloudflare
        string dotnet = new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"), Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "dotnet") }
            .Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d, "dotnet.exe")).FirstOrDefault(File.Exists);
        if (dotnet == null)
            Console.WriteLine("SKIP: dotnet.exe not found, the check with a third-party embedded signature was not run");
        else
        {
            var other = Authenticode.Verify(dotnet);
            if (!other.Trusted)
                Console.WriteLine("SKIP: dotnet.exe signature is not trusted on this machine (" + other.Problem + ")");
            else
            {
                Check(other.SignerSubject != null && other.SignerSubject.Contains("Microsoft"), "The signer of dotnet.exe must be read: " + other.SignerSubject);
                var notCloudflare = Catch(() => Sync(() => (Task<string>)typeof(WarpInstaller).GetMethod("VerifySignatureAsync", PrivateStatic).Invoke(null, new object[] { dotnet })));
                Check(notCloudflare != null && notCloudflare.Message.Contains("Microsoft"), "A valid signature of another publisher must be rejected: " + notCloudflare?.Message);
            }
        }
    }

    static void TestWarpDownload()
    {
        var payload = new byte[700 * 1024];
        new Random(42).NextBytes(payload);
        string expected;
        using (var sha = System.Security.Cryptography.SHA256.Create()) expected = string.Concat(sha.ComputeHash(payload).Select(b => b.ToString("x2")));

        var download = typeof(WarpInstaller).GetMethod("DownloadAsync", PrivateStatic);
        var withRetry = typeof(WarpInstaller).GetMethod("DownloadWithRetryAsync", PrivateStatic);
        string file = Path.Combine(_data, "download.bin");
        using (var server = new FakeServer(payload))
        {
            string Get(MethodInfo method, string path, long min = 100 * 1024, long max = 10 << 20, double stallSeconds = 5,
                List<int> percent = null, CancellationToken ct = default(CancellationToken)) =>
                Sync(() => (Task<string>)method.Invoke(null, new object[]
                {
                    server.Url(path), file, min, max, TimeSpan.FromSeconds(stallSeconds),
                    percent == null ? null : new SyncProgress<int>(percent.Add), ct,
                }));

            var steps = new List<int>();
            Check(Get(download, "/ok", percent: steps) == expected, "The reported SHA-256 must match the data");
            Check(File.ReadAllBytes(file).SequenceEqual(payload), "The file on disk must match the data");
            Check(steps.Count > 1 && steps.Last() == 100 && steps.SequenceEqual(steps.OrderBy(p => p)), "Progress must grow monotonically up to 100%");
            File.Delete(file);
            Check(Get(download, "/redirect") == expected, "Redirects must be followed");

            var missing = Catch(() => Get(download, "/missing"));
            Check(missing != null && missing.Message == L.T("err.warpHttp", 404), "HTTP 404 must be reported: " + missing?.Message);
            var broken = Catch(() => Get(download, "/server-error"));
            Check(broken != null && broken.Message.Contains("500"), "HTTP 500 must be reported: " + broken?.Message);
            var tiny = Catch(() => Get(download, "/tiny"));
            Check(tiny != null && tiny.Message == L.T("err.warpSize", 100), "A too small installer must be rejected: " + tiny?.Message);
            var huge = Catch(() => Get(download, "/ok", max: 100 * 1024));
            Check(huge != null && huge.Message == L.T("err.warpSize", payload.Length), "A too large installer must be rejected: " + huge?.Message);
            Check(Catch(() => Get(download, "/truncated")) != null, "A connection cut mid-download must be an error");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var stalled = Catch(() => Get(download, "/stall", stallSeconds: 1));
            Check(stalled != null && stalled.Message == L.T("err.warpStalled") && clock.Elapsed.TotalSeconds < 10,
                "A stalled download must fail with the stall message within seconds: " + stalled?.Message);

            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(400);
                clock.Restart();
                var cancelled = Catch(() => Get(download, "/stall", stallSeconds: 30, ct: cts.Token));
                Check(cancelled is OperationCanceledException && clock.Elapsed.TotalSeconds < 10, "Cancelling must interrupt a stalled download: " + cancelled?.GetType().Name);
            }

            // отказ сервера не повторяется (повторы заняли бы секунды), обрыв соединения повторяется
            clock.Restart();
            Check(Catch(() => Get(withRetry, "/missing")) != null && clock.Elapsed.TotalSeconds < 5, "HTTP 404 must not be retried");
        }
    }

    /// <summary>Крошечная программа вместо warp-cli.exe: завершается с нужным кодом, что бы ей ни передали.</summary>
    static string MakeFakeCli(string folder, int exitCode)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "warp-cli.exe");
        var result = new Microsoft.CSharp.CSharpCodeProvider().CompileAssemblyFromSource(
            new System.CodeDom.Compiler.CompilerParameters { GenerateExecutable = true, OutputAssembly = path },
            "static class P { static int Main() { return " + exitCode + "; } }");
        if (result.Errors.HasErrors) throw new Exception("Could not build the fake warp-cli: " + result.Errors[0]);
        return path;
    }

    static void TestEnsureWarp()
    {
        string cliOk = MakeFakeCli(Path.Combine(_data, "fake-ok"), 0);
        string cliDead = MakeFakeCli(Path.Combine(_data, "fake-dead"), 1);
        var ensure = typeof(Engine).GetMethod("EnsureWarpAsync", PrivateInstance);
        bool Ensure(Engine e) => Sync(() => (Task<bool>)ensure.Invoke(e, new object[] { CancellationToken.None }));
        var installField = typeof(Engine).GetField("InstallWarp", PrivateInstance);
        var readyField = typeof(Engine).GetField("WarpReadyTimeoutMs", PrivateInstance);
        void Install(Engine e, Func<IProgress<WarpStep>, CancellationToken, Task> install) => installField.SetValue(e, install);
        Task Nothing(IProgress<WarpStep> p, CancellationToken ct) => Task.CompletedTask;

        try
        {
            // WARP на месте: ничего не спрашивается и не ставится
            SetFinder(() => Found(cliOk));
            var engine = NewEngine();
            int asked = 0, installed = 0;
            engine.AskInstallWarp = () => { asked++; return true; };
            Install(engine, (p, ct) => { installed++; return Task.CompletedTask; });
            Check(Ensure(engine) && asked == 0 && installed == 0 && engine.Warp.CliPath == cliOk, "Installed WARP must be used without asking");

            // WARP нет, спросить некого или пользователь отказался
            SetFinder(Missing);
            engine = NewEngine();
            Install(engine, (p, ct) => { installed++; return Task.CompletedTask; });
            Check(!Ensure(engine) && installed == 0 && engine.State == EngineState.Idle && engine.Detail == L.T("detail.noWarp"),
                "Without a way to ask, nothing is installed");
            engine.AskInstallWarp = () => { asked++; return false; };
            Check(!Ensure(engine) && asked == 1 && installed == 0 && engine.Detail == L.T("detail.noWarp"), "A declined installation must not install anything");

            // согласие: установщик запущен, WARP появился, служба отвечает
            asked = installed = 0;
            engine.AskInstallWarp = () => { asked++; return true; };
            Install(engine, (p, ct) =>
            {
                installed++;
                p.Report(new WarpStep(new Msg("progress.warpDownloading", 50), 50));
                SetFinder(() => Found(cliOk)); // установщик «поставил» WARP
                return Task.CompletedTask;
            });
            bool ready = Ensure(engine);
            Thread.Sleep(200); // дать запоздавшему отчёту о ходе загрузки дойти: он не должен вернуть полосу
            Check(ready && asked == 1 && installed == 1, "An accepted installation must ask once and install once");
            Check(engine.Warp.CliPath == cliOk, "After installing, the new WARP must be found");
            Check(engine.ProgressTotal == 0, "A late progress report must not bring the download bar back");

            // сбой установки (подпись, сеть, код msiexec): понятная ошибка, WARP не появился
            SetFinder(Missing);
            engine = NewEngine();
            engine.AskInstallWarp = () => true;
            Install(engine, (p, ct) => { throw new Exception("signature check failed"); });
            Check(!Ensure(engine) && engine.State == EngineState.Idle && engine.Detail == L.T("detail.warpInstallFailed"), "A failed installation must be reported");

            // установщик отработал, но warp-cli так и не найден
            Install(engine, Nothing);
            Check(!Ensure(engine) && engine.Detail == L.T("detail.warpInstallFailed"), "An installer that leaves no WARP behind must be reported");

            // WARP установлен, но служба не отвечает
            readyField.SetValue(engine, 1500);
            Install(engine, (p, ct) => { SetFinder(() => Found(cliDead)); return Task.CompletedTask; });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Check(!Ensure(engine) && engine.Detail == L.T("detail.warpInstallFailed") && clock.Elapsed.TotalSeconds < 30, "A service that never answers must be reported");

            // отмена не превращается в «ошибку установки»
            SetFinder(Missing);
            typeof(Warp).GetField("<CliPath>k__BackingField", PrivateInstance).SetValue(engine.Warp, null); // прошлый шаг оставил путь
            Install(engine, (p, ct) => { throw new OperationCanceledException(); });
            Check(Catch(() => Ensure(engine)) is OperationCanceledException, "Cancelling must pass through");
        }
        finally
        {
            SetFinder(Missing);
        }
    }

    static void TestInstallHint()
    {
        var engine = NewEngine();
        using (var main = NewMain(engine))
        {
            var hint = (Label)main.GetType().GetField("_hint", PrivateInstance).GetValue(main);
            Check(hint.Text == L.T("hint.installWarp"), "Without WARP the hint must say Zarp will install it: " + hint.Text);
            typeof(Warp).GetField("<CliPath>k__BackingField", PrivateInstance).SetValue(engine.Warp, @"C:\Fake\warp-cli.exe");
            main.GetType().GetMethod("UpdateUi", PrivateInstance).Invoke(main, null);
            Check(hint.Text == L.T("hint.firstRun"), "With WARP present the usual first-run hint returns: " + hint.Text);
        }
    }

    sealed class SyncProgress<T> : IProgress<T>
    {
        readonly Action<T> _action;
        public SyncProgress(Action<T> action) { _action = action; }
        public void Report(T value) => _action(value);
    }

    /// <summary>Минимальный HTTP-сервер на localhost: ответы, редирект, ошибки, обрыв и зависание.</summary>
    sealed class FakeServer : IDisposable
    {
        readonly System.Net.Sockets.TcpListener _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        readonly ManualResetEvent _stop = new ManualResetEvent(false);
        readonly byte[] _payload;

        public FakeServer(byte[] payload)
        {
            _payload = payload;
            _listener.Start();
            new Thread(Accept) { IsBackground = true }.Start();
        }

        string Base => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Url(string path) => Base + path;

        void Accept()
        {
            try
            {
                while (true)
                {
                    var client = _listener.AcceptTcpClient();
                    new Thread(() => Handle(client)) { IsBackground = true }.Start();
                }
            }
            catch { } // сервер остановлен
        }

        void Handle(System.Net.Sockets.TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    stream.ReadTimeout = 5000;
                    var request = new System.Text.StringBuilder();
                    var one = new byte[1];
                    while (!request.ToString().EndsWith("\r\n\r\n") && stream.Read(one, 0, 1) == 1) request.Append((char)one[0]);
                    string path = request.ToString().Split('\n')[0].Split(' ')[1];

                    void Headers(string status, long length, string extra = "")
                    {
                        var head = System.Text.Encoding.ASCII.GetBytes(
                            "HTTP/1.1 " + status + "\r\nContent-Length: " + length + "\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n" + extra + "\r\n");
                        stream.Write(head, 0, head.Length);
                    }

                    switch (path)
                    {
                        case "/ok":
                            Headers("200 OK", _payload.Length);
                            stream.Write(_payload, 0, _payload.Length);
                            break;
                        case "/redirect":
                            Headers("302 Found", 0, "Location: " + Url("/ok") + "\r\n");
                            break;
                        case "/missing":
                            Headers("404 Not Found", 0);
                            break;
                        case "/server-error":
                            Headers("500 Internal Server Error", 0);
                            break;
                        case "/tiny":
                            Headers("200 OK", 100);
                            stream.Write(_payload, 0, 100);
                            break;
                        case "/truncated":
                            Headers("200 OK", _payload.Length);
                            stream.Write(_payload, 0, _payload.Length / 2);
                            break; // соединение закрывается раньше времени
                        case "/stall":
                            Headers("200 OK", _payload.Length);
                            stream.Write(_payload, 0, 100 * 1024);
                            stream.Flush();
                            _stop.WaitOne(30000); // дальше ни байта
                            break;
                    }
                }
            }
            catch { } // клиент оборвал соединение
        }

        public void Dispose()
        {
            _stop.Set();
            _listener.Stop();
        }
    }

    static void TestExitDuringPrompt()
    {
        var engine = NewEngine();
        using (var main = NewMain(engine))
        using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
        {
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                main.GetType().GetMethod("ExitApp", PrivateInstance).Invoke(main, null);
            };
            timer.Start();
            main.Close();
            Application.DoEvents();
            Check(main.IsDisposed, "Explicit exit must dismiss an already open close dialog");
            Check(engine.Config.AskBeforeClose, "Dismissing a dialog for exit must not save its choice");
        }
    }

    static void SaveImage(Control form, string name)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name));
        }
    }

    static void CaptureControls()
    {
        using (var main = NewMain(NewEngine())) SaveImage(main, "main.png");
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        using (var preview = new Form { BackColor = Color.FromArgb(18, 20, 25), ClientSize = new Size((int)(330 * scale), (int)(90 * scale)) })
        {
            PositionOffscreen(preview);
            var gear = (Control)Activator.CreateInstance(AppAssembly.GetType("Zarp.UI.SettingsButton"));
            gear.Size = new Size((int)(40 * scale), (int)(40 * scale));
            gear.Location = new Point((int)(12 * scale), (int)(12 * scale));
            var select = (Button)Activator.CreateInstance(AppAssembly.GetType("Zarp.UI.DarkSelect"),
                new object[] { new[] { "Спрашивать каждый раз", "Скрывать в трей", "Закрывать приложение" } });
            select.Font = new Font("Segoe UI", 9f * scale);
            select.Size = new Size((int)(240 * scale), (int)(32 * scale));
            select.Location = new Point((int)(66 * scale), (int)(16 * scale));
            select.GetType().GetProperty("SelectedIndex").SetValue(select, 2);
            preview.Controls.AddRange(new[] { gear, select });
            preview.Show();
            Application.DoEvents();
            SaveImage(preview, "controls-" + (int)(scale * 100) + ".png");
            select.PerformClick();
            var menu = (ContextMenuStrip)select.GetType().GetField("_menu", PrivateInstance).GetValue(select);
            Application.DoEvents();
            SaveImage(menu, "dropdown-" + (int)(scale * 100) + ".png");
            menu.Close();
        }
    }

    static void TestShutdownDuringOperation()
    {
        var engine = NewEngine();
        var completion = new TaskCompletionSource<bool>();
        bool cancellationRequested = false;
        Func<CancellationToken, Task> pending = ct =>
        {
            ct.Register(() => cancellationRequested = true);
            return completion.Task;
        };
        var run = typeof(Engine).GetMethod("Run", PrivateInstance);
        var operation = (Task)run.Invoke(engine, new object[] { pending });
        using (var main = NewMain(engine))
        {
            engine.Config.AskBeforeClose = false;
            engine.Config.MinimizeToTray = false;
            main.Close();
            Check(cancellationRequested, "Exit must request cancellation of the active operation");
            Check(!main.IsDisposed, "Exit must wait for operation cleanup");
            main.Close();
            Check(!main.IsDisposed, "Repeated close must not interrupt cleanup");
            bool startedAfterExit = false;
            Func<CancellationToken, Task> forbidden = ct =>
            {
                startedAfterExit = true;
                return Task.CompletedTask;
            };
            run.Invoke(engine, new object[] { forbidden });
            Check(!startedAfterExit, "New operations must be rejected during shutdown");
            completion.SetResult(true);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!main.IsDisposed && DateTime.UtcNow < deadline)
                Application.DoEvents();
            Check(operation.IsCompleted && main.IsDisposed, "Exit must finish after operation cleanup");
            run.Invoke(engine, new object[] { forbidden });
            Check(!startedAfterExit, "New operations must be rejected after shutdown");
        }
    }
}
