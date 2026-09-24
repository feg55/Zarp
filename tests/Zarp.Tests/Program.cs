using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
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
            Console.Error.WriteLine("FAIL: UI checks did not finish within 30 seconds.");
            Environment.Exit(1);
        }, null, 30000, System.Threading.Timeout.Infinite);

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
                    else prompt.Controls.OfType<Button>().Single(b => b.Text == (tray ? "Скрыть в трей" : "Закрыть приложение")).PerformClick();
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
            var choice = (ComboBox)settings.GetType().GetField("_closeAction", PrivateInstance).GetValue(settings);
            choice.SelectedIndex = 2;
            Check(!engine.Config.AskBeforeClose && !engine.Config.MinimizeToTray,
                "Exit preference must save while the autostart query is pending");
            choice.SelectedIndex = 1;
            Check(!engine.Config.AskBeforeClose && engine.Config.MinimizeToTray, "Settings must support tray by default");
            choice.SelectedIndex = 0;
            Check(engine.Config.AskBeforeClose, "Settings must restore prompting");
            Application.DoEvents();
            Check(choice.Parent.ClientRectangle.Contains(choice.Bounds), "Close dropdown must fit in its row");
            foreach (Control control in choice.Parent.Parent.Controls)
                Check(choice.Parent.Parent.ClientRectangle.Contains(control.Bounds), "Settings option is clipped: " + control.Text);
            SaveImage(settings, "settings.png");
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

    static void SaveImage(Form form, string name)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name));
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
