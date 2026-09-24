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
            Console.Error.WriteLine("FAIL: UI checks did not finish within 60 seconds.");
            Environment.Exit(1);
        }, null, 60000, System.Threading.Timeout.Infinite);

        // Isolate UI tests from embedded driver extraction, WARP, and background downloads.
        typeof(Zapret).GetField("_embeddedVersion", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, "");
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
