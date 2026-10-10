using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Zarp.Core;

// Окна маршрутизации, пресетов, баз GeoIP и GeoSite и выбора программ.
static partial class Program
{
    /// <summary>Список программ без просмотра процессов и ярлыков: быстро и одинаково на любой машине.</summary>
    static List<AppEntry> FixedApps(params string[] paths) => paths.Select(p => new AppEntry { Path = p, Name = Path.GetFileNameWithoutExtension(p) }).ToList();

    static void UseFixedApps(List<AppEntry> apps) =>
        AppAssembly.GetType("Zarp.UI.AppsForm").GetField("Source", PrivateStatic).SetValue(null, (Func<List<AppEntry>>)(() => apps));

    static string SystemTool(string name) => Path.Combine(Environment.SystemDirectory, name);

    static void TestInstalledApps()
    {
        var apps = InstalledApps.Enumerate();
        Check(apps.Count > 0, "Some programs must be found");
        Check(apps.All(a => a.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(a.Path) && !string.IsNullOrWhiteSpace(a.Name)), "Every entry is an existing program with a name");
        Check(apps.Select(a => a.Path.ToLowerInvariant()).Distinct().Count() == apps.Count, "Programs are listed once, ignoring the case of the path");
        Check(!apps.Any(a => Path.GetFileName(a.Path).StartsWith("unins", StringComparison.OrdinalIgnoreCase)), "Uninstallers are not offered");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
        Check(!apps.Any(a => a.Path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(a.Path), "svchost.exe", StringComparison.OrdinalIgnoreCase)),
            "Windows service processes without a window are not offered");
        string me = Assembly.GetExecutingAssembly().Location;
        Check(apps.Any(a => string.Equals(a.Path, me, StringComparison.OrdinalIgnoreCase)), "A running program outside the Windows folder is found: " + me);
        var names = apps.Select(a => a.Name).ToList();
        Check(names.SequenceEqual(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), "The list is sorted by name");
        var described = InstalledApps.Describe(SystemTool("notepad.exe"));
        Check(!string.IsNullOrWhiteSpace(described.Name) && described.Path == SystemTool("notepad.exe"), "A program is named by its description: " + described.Name);
        Check(InstalledApps.Describe(Path.Combine(_data, "no-such-program.exe")).Name == "no-such-program", "Without a description the file name is used");
    }

    static void TestRoutingForm()
    {
        var engine = NewEngine();
        using (var form = NewForm("RoutingForm", engine))
        {
            PositionOffscreen(form);
            form.Show();
            Application.DoEvents();
            var list = GetPrivate<ListView>(form, "_list");
            var use = GetPrivate<Button>(form, "_use");
            var edit = GetPrivate<Button>(form, "_edit");
            var remove = GetPrivate<Button>(form, "_remove");
            var enable = GetPrivate<Control>(form, "_enable");
            Check(list.Items.Count == 4 && list.SelectedItems.Count == 1 && ((RoutingPreset)list.SelectedItems[0].Tag).Id == "lan" && list.Items[0].Text == "✔",
                "The four presets are listed and the active one is marked and selected");
            Check(list.Items[1].SubItems[1].Text == L.T("country.ru") && list.Items[0].SubItems[1].Text == L.T("routing.lanPreset") && list.Items[1].SubItems[2].Text == "2" && list.Items[0].SubItems[3].Text == "0",
                "Built-in presets are named from the translations and show their rule counts");
            Check(!use.Enabled && edit.Enabled && !remove.Enabled && remove.Text == L.T("routing.resetPreset"), "The active unmodified built-in preset can be edited, not used again or reset");

            list.Items[3].Selected = true; // Китай
            Check(use.Enabled, "Another preset can be used");
            use.PerformClick();
            Check(engine.Config.Routing.SelectedPreset == "cn" && list.Items[3].Text == "✔" && !use.Enabled, "Use makes the preset active");
            Check(AppConfig.Load(Path.Combine(engine.DataDir, "zarp.json")).Routing.SelectedPreset == "cn", "The choice is saved");
            SetChecked(enable, true);
            Check(engine.Config.Routing.Enabled && AppConfig.Load(Path.Combine(engine.DataDir, "zarp.json")).Routing.Enabled, "The switch enables routing and is saved");

            // изменённый встроенный набор можно вернуть к исходному
            engine.Config.Routing.Presets[3] = engine.Config.Routing.Presets[3].WithRules("block", "geosite:category-ads-all");
            form.GetType().GetMethod("FillList", PrivateInstance).Invoke(form, new object[] { "cn" });
            Check(list.Items[3].SubItems[4].Text == "1" && remove.Enabled && remove.Text == L.T("routing.resetPreset"), "An edited built-in preset can be reset");
            remove.PerformClick();
            Check(engine.Config.Routing.Presets[3].SameRules(RoutingSettings.Original("cn")) && !remove.Enabled && list.Items[3].SubItems[4].Text == "0", "Reset restores the original rules");

            // свой набор: удаляется, а не сбрасывается; название кнопки меняется
            engine.Config.Routing.Presets.Add(new RoutingPreset { Id = "custom-x", Name = "Work", Direct = "domain:corp.example" });
            form.GetType().GetMethod("FillList", PrivateInstance).Invoke(form, new object[] { "custom-x" });
            Check(list.Items.Count == 5 && list.Items[4].SubItems[1].Text == "Work" && remove.Text == L.T("routing.deletePreset") && remove.Enabled, "A custom preset is listed under its own name and can be deleted");
        }
    }

    static void TestPresetForm()
    {
        // свой набор
        var custom = new RoutingPreset { Id = "custom-1", Name = "Work", Direct = "domain:corp.example", Block = "# ads\ngeosite:category-ads-all" };
        using (var form = NewForm("PresetForm", custom, false))
        {
            PositionOffscreen(form);
            form.Show();
            Application.DoEvents();
            var name = GetPrivate<Control>(form, "_name");
            var direct = GetPrivate<Control>(form, "_direct");
            var proxy = GetPrivate<Control>(form, "_proxy");
            var block = GetPrivate<Control>(form, "_block");
            var error = GetPrivate<Label>(form, "_error");
            var save = GetPrivate<Button>(form, "_save");
            var reset = GetPrivate<Button>(form, "_reset");
            Check(name.Parent == form && name.Text == "Work" && !reset.Visible && direct.Text == "domain:corp.example" && block.Text.Contains("category-ads-all"), "A custom preset shows its name and rules and has no reset");
            Check((int)name.GetType().GetProperty("MaxLength").GetValue(name) == 80, "A preset name is limited to 80 characters, like the stored one");
            direct.Text = "domain:corp.example\nbad rule here";
            save.PerformClick();
            Check(error.Text.StartsWith(L.T("routing.invalidRules") + ": " + L.T("routing.direct") + ", " + L.T("routing.line", 2)) &&
                  form.DialogResult == DialogResult.None && PrivateResult(form) == null, "A rule error names the group and the line and keeps the dialog open: " + error.Text);
            direct.Text = "domain:corp.example";
            Check(error.Text == "", "Editing clears the old error");
            proxy.Text = "  full:Corp.Example  \n";
            name.Text = "  ";
            save.PerformClick();
            var result = PrivateResult(form);
            Check(form.DialogResult == DialogResult.OK && result != null && result.Id == "custom-1" && result.Name == L.T("routing.customPreset") && result.Proxy == "full:Corp.Example" &&
                  result.Direct == "domain:corp.example", "Saving trims the text and gives an unnamed preset the default name");
            name.Text = "Office";
            save.PerformClick();
            Check(PrivateResult(form).Name == "Office", "A custom name is kept");
        }

        // встроенный набор: названия не меняют, правила можно вернуть
        var builtIn = RoutingSettings.Original("ru").WithRules("direct", "geoip:ru");
        using (var form = NewForm("PresetForm", builtIn, false))
        {
            PositionOffscreen(form);
            form.Show();
            Application.DoEvents();
            var name = GetPrivate<Control>(form, "_name");
            var direct = GetPrivate<Control>(form, "_direct");
            var reset = GetPrivate<Button>(form, "_reset");
            var save = GetPrivate<Button>(form, "_save");
            Check(name.Parent != form && reset.Visible && direct.Text == "geoip:ru", "A built-in preset has a fixed name and can be reset");
            reset.PerformClick();
            Check(direct.Text.Replace("\r", "") == "geoip:private\ngeoip:ru", "Reset puts the original rules back into the editor");
            save.PerformClick();
            Check(PrivateResult(form).Name == "" && PrivateResult(form).SameRules(RoutingSettings.Original("ru")), "A built-in preset keeps an empty name");
        }
    }

    static RoutingPreset PrivateResult(Form form) => (RoutingPreset)form.GetType().GetProperty("Result").GetValue(form);

    static void TestGeoSourcesForm()
    {
        byte[] valid = File.ReadAllBytes(TestGeoIpFile());
        var http = new ScriptedHttp { Respond = r => Ok(valid) };
        var engine = NewEngine();
        typeof(GeoData).GetField("HandlerFactory", PrivateInstance).SetValue(engine.Geo, (Func<HttpMessageHandler>)(() => new NonDisposing(http)));
        using (var form = NewForm("GeoSourcesForm", engine))
        {
            PositionOffscreen(form);
            form.Show();
            Application.DoEvents();
            var sections = (System.Collections.IList)GetPrivate<object>(form, "_sections");
            object Geoip() => sections[0];
            T Member<T>(object section, string field) => (T)section.GetType().GetField(field).GetValue(section);
            var url = Member<Control>(Geoip(), "Url");
            var status = Member<Label>(Geoip(), "Status");
            var error = Member<Label>(Geoip(), "Error");
            var download = Member<Button>(Geoip(), "Download");
            var reset = Member<Button>(Geoip(), "Reset");
            Check(url.Text == RoutingSettings.DefaultGeoipUrl && status.Text == L.T("geo.bundled") && download.Text == L.T("geo.download") && download.Enabled && !reset.Visible,
                "The default source shows the bundled ranges until a database is downloaded");
            Check(Member<Label>(sections[1], "Status").Text == L.T("geo.missing"), "GeoSite always needs a download");

            // адрес записывается, как только он верен; неверный объясняется и скачать его нельзя
            url.Text = "http://insecure.example/geoip.dat";
            Check(status.Text == L.T("geo.invalidUrl") && !download.Enabled && engine.Config.Routing.GeoipUrl == RoutingSettings.DefaultGeoipUrl, "An unsafe address is explained, not saved and cannot be downloaded");
            url.Text = "https://mirror.example/geoip.dat";
            Check(engine.Config.Routing.GeoipUrl == "https://mirror.example/geoip.dat" && status.Text == L.T("geo.missing") && download.Enabled && reset.Visible, "A good address is saved at once; a new address has no data yet");
            Check(AppConfig.Load(Path.Combine(engine.DataDir, "zarp.json")).Routing.GeoipUrl == "https://mirror.example/geoip.dat", "The address survives a restart");

            // загрузка и обновление
            download.PerformClick();
            PumpUntil(() => engine.Geo.State("geoip", "https://mirror.example/geoip.dat").Exists && !engine.Geo.AnyDownloading, "The download");
            Application.DoEvents();
            string ready = L.T("geo.ready", 3, "@@");
            Check(status.Text.StartsWith(ready.Substring(0, ready.IndexOf("@@"))) && download.Text == L.T("geo.update") && error.Text == "", "A downloaded database shows its category count: " + status.Text);
            http.Respond = r => Ok(Array.ConvertAll(new byte[40], b => (byte)'x'));
            download.PerformClick();
            PumpUntil(() => !engine.Geo.AnyDownloading && engine.Geo.State("geoip", "https://mirror.example/geoip.dat").Error != null, "The failed update");
            Application.DoEvents();
            Check(error.Text == L.T("geo.failed") && status.Text.StartsWith(ready.Substring(0, ready.IndexOf("@@"))) && File.ReadAllBytes(engine.Geo.DataFile("geoip", "https://mirror.example/geoip.dat")).SequenceEqual(valid),
                "A failed update reports it and keeps the previous database: " + error.Text + " / " + status.Text);
            reset.PerformClick();
            Check(url.Text == RoutingSettings.DefaultGeoipUrl && engine.Config.Routing.GeoipUrl == RoutingSettings.DefaultGeoipUrl && !reset.Visible, "The default source button restores the standard address");
        }
    }

    static void TestAppsForm()
    {
        string notepad = SystemTool("notepad.exe"), cmd = SystemTool("cmd.exe"), gone = Path.Combine(_data, "Gone App", "gone.exe");
        UseFixedApps(FixedApps(notepad, cmd));
        var engine = NewEngine();
        engine.Config.ProxyApps = new List<string> { gone };
        using (var form = NewForm("AppsForm", engine))
        {
            PositionOffscreen(form);
            form.Show();
            var summary = GetPrivate<Label>(form, "_summary");
            Check(summary.Text == L.T("apps.loading"), "The list is loaded in the background: " + summary.Text);
            PumpUntil(() => GetPrivate<bool>(form, "_loaded"), "Loading the program list");
            var list = GetPrivate<ListView>(form, "_list");
            var search = GetPrivate<Control>(form, "_search");
            var enable = GetPrivate<Control>(form, "_enable");
            var toggle = form.GetType().GetMethod("ToggleSelected", PrivateInstance);
            Check(list.Items.Count == 3 && summary.Text == L.T("apps.empty") && summary.ForeColor == Color.FromArgb(128, 134, 146), "Found programs and a selected one that is gone are listed; nothing usable is selected");
            Check(list.Items.Cast<ListViewItem>().Any(i => i.SubItems[1].Text.EndsWith(L.T("apps.missing")) && i.Text == "✔"), "A selected program that no longer exists is marked as missing");

            // выбор записывается сразу
            var row = list.Items.Cast<ListViewItem>().Single(i => ((AppEntry)i.Tag).Path == notepad);
            row.Selected = true;
            toggle.Invoke(form, null);
            Check(engine.Config.ProxyApps.SequenceEqual(new[] { gone, notepad }) && summary.Text == L.T("apps.selected", 1), "Choosing a program saves the choice and counts only existing programs");
            Check(AppConfig.Load(Path.Combine(engine.DataDir, "zarp.json")).ProxyApps.Contains(notepad), "The program list survives a restart");
            Check(list.Items.Cast<ListViewItem>().Single(i => ((AppEntry)i.Tag).Path == notepad).Text == "✔", "A chosen program is marked");
            toggle.Invoke(form, null);
            Check(engine.Config.ProxyApps.SequenceEqual(new[] { gone }) && summary.Text == L.T("apps.empty"), "Choosing it again removes it");

            // поиск по названию и по пути
            search.Text = "cmd";
            Check(list.Items.Count == 1 && ((AppEntry)list.Items[0].Tag).Path == cmd, "Search by name");
            search.Text = "gone app";
            Check(list.Items.Count == 1 && ((AppEntry)list.Items[0].Tag).Path == gone, "Search by path");
            search.Text = "zzz-nothing";
            Check(list.Items.Count == 0, "Nothing matches");
            search.Text = "";

            SetChecked(enable, true);
            Check(engine.Config.PerAppProxy && summary.ForeColor == Color.FromArgb(232, 84, 84), "With the mode on and no usable program the problem is shown in red");
            Check(AppConfig.Load(Path.Combine(engine.DataDir, "zarp.json")).PerAppProxy, "The mode is saved");
        }
        UseFixedApps(FixedApps());
    }
}
