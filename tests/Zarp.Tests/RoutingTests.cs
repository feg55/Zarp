using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zarp.Core;

// Маршрутизация: правила, базы .dat, загрузка баз. Правила проверяются и настоящим sing-box на localhost, без адаптера.
static partial class Program
{
    // ------------------------------------------------------------------ минимальный писатель protobuf для файлов .dat

    static byte[] PbVarint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    static byte[] PbJoin(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static byte[] PbBytes(int field, byte[] value) => PbJoin(PbVarint((ulong)(field << 3 | 2)), PbVarint((ulong)value.Length), value);
    static byte[] PbText(int field, string value) => PbBytes(field, Encoding.UTF8.GetBytes(value));
    static byte[] PbInt(int field, ulong value) => PbJoin(PbVarint((ulong)(field << 3)), PbVarint(value));

    static byte[] CidrRecord(string address, int prefix) => PbJoin(PbBytes(1, IPAddress.Parse(address).GetAddressBytes()), PbInt(2, (ulong)prefix));

    static byte[] DomainRecord(int type, string value, params string[] attributes) =>
        PbJoin(new[] { PbInt(1, (ulong)type), PbText(2, value) }.Concat(attributes.Select(a => PbBytes(3, PbJoin(PbText(1, a), PbInt(2, 1))))).ToArray());

    static byte[] GeoCategory(string name, bool inverse, params byte[][] records) =>
        PbJoin(new[] { PbText(1, name) }.Concat(records.Select(r => PbBytes(2, r))).Concat(inverse ? new[] { PbInt(3, 1) } : new byte[0][]).ToArray());

    static string WriteGeoFile(string name, params byte[][] categories)
    {
        string path = Path.Combine(_data, name);
        File.WriteAllBytes(path, PbJoin(categories.Select(c => PbBytes(1, c)).ToArray()));
        return path;
    }

    static string TestGeoIpFile() => WriteGeoFile("test-geoip.dat",
        GeoCategory("TEST", false, CidrRecord("127.0.0.0", 8), CidrRecord("2001:db8::", 32), CidrRecord("10.1.2.3", 8)),
        GeoCategory("inv", true, CidrRecord("10.0.0.0", 8)),
        GeoCategory("other", false, CidrRecord("192.0.2.0", 24)));

    static string TestGeoSiteFile() => WriteGeoFile("test-geosite.dat",
        GeoCategory("test", false,
            DomainRecord(2, "example.org", "ads"),
            DomainRecord(3, "full.example.org"),
            DomainRecord(1, "^a[0-9]+\\.example\\.com$"),
            DomainRecord(0, "tracker"),
            DomainRecord(2, "plain.example.net")),
        GeoCategory("cn", false, DomainRecord(2, "baidu.com", "cn"), DomainRecord(2, "google.com", "ads"), DomainRecord(2, "qq.com", "cn")));

    static RoutingPreset Preset(string direct = "", string proxy = "", string block = "") =>
        new RoutingPreset { Id = "t", Direct = direct, Proxy = proxy, Block = block };

    static void TestRouteRuleParsing()
    {
        RouteToken Parse(string text) => RouteRules.ParseToken(text);
        var geoip = Parse("GeoIP:CN");
        Check(geoip.Kind == "geoip" && geoip.Value == "cn" && geoip.Attr == "", "Rule types and categories are case insensitive");
        var site = Parse("geosite:Google@!CN");
        Check(site.Kind == "geosite" && site.Value == "google" && site.Attr == "!cn", "A GeoSite category may carry an attribute filter, and ! negates it");
        Check(Parse("domain:Example.com").Kind == "domain_suffix" && Parse("domain:Example.com").Value == "example.com", "domain: matches the name and its subdomains");
        Check(Parse("Example.com.").Kind == "domain_suffix" && Parse("Example.com.").Value == "example.com", "A bare name is a domain; the trailing dot is dropped");
        Check(Parse("full:Example.com").Kind == "domain" && Parse("keyword:ads").Kind == "domain_keyword" && Parse("regexp:^ads[0-9]+\\.").Kind == "domain_regex", "full, keyword and regexp kinds");
        Check(Parse("1.2.3.4").Value == "1.2.3.4/32" && Parse("10.1.2.3/8").Value == "10.0.0.0/8" && Parse("2001:db8::/32").Value == "2001:db8::/32" && Parse("2001:db8::1").Value == "2001:db8::1/128",
            "Addresses and ranges are normalized");
        Check(Parse("  geoip:private  ").Value == "private" && Parse("my_host-1.example").Value == "my_host-1.example", "Spaces are trimmed; underscores and digits are fine in labels");

        string[] bad =
        {
            "1.2.3.4/999", "regexp:[", "geosite:cn@", "https://example.com", "999.1.2.3", "geoip:cn@x", "domain:exa mple.com", "domain:\u043f\u0440\u0438\u043c\u0435\u0440.\u0440\u0444",
            "domain:-bad.com", "domain:bad-.com", "domain:a..b", "full:", "keyword:", "regexp:", "unknown:foo", "regexp:^(?=a)b", "regexp:(a)\\1", "10.0.0.0/08",
            "01.2.3.4", "geoip:", "geosite:bad name", "geosite:x@!", "1.2.3", "fe80::1%2", ":", "geoip:cn:extra", new string('a', 4097),
        };
        foreach (var rule in bad)
            Check(Catch(() => Parse(rule)) is RouteRuleException, "Must be rejected: " + (rule.Length > 40 ? rule.Substring(0, 40) : rule));

        // ошибка называет группу и строку, как их видит пользователь (вместе с комментариями и пустыми строками)
        var failure = Catch(() => RouteRules.Validate(Preset(block: "# ads\n\ngeoip:cn\nbad rule here")));
        Check(failure is RouteRuleException && failure.Message.StartsWith(L.T("routing.block") + ", " + L.T("routing.line", 4) + ":"), "The message names the group and the real line: " + failure?.Message);
        Check(Catch(() => RouteRules.Validate(Preset(direct: "geoip:private\ngeoip:ru", proxy: "full:a.example\n# note"))) == null, "Valid rules pass without any database");
        Check(Catch(() => RouteRules.Validate(Preset(direct: string.Join("\n", Enumerable.Range(0, 10001).Select(i => "a" + i + ".example"))))) is RouteRuleException, "An absurd number of rules is refused");
    }

    static void TestRoutingSettings()
    {
        var defaults = RoutingSettings.Defaults();
        Check(defaults.Select(p => p.Id).SequenceEqual(new[] { "lan", "ru", "ir", "cn" }) && defaults[0].Direct == "geoip:private" &&
              defaults[2].Direct == "geoip:private\ngeoip:ir", "Built-in presets: local network and three countries, which keep local addresses direct");
        var settings = new RoutingSettings();
        Check(!settings.Enabled && settings.Active() == null && settings.SelectedPreset == "lan", "Routing is off at first");
        settings.Enabled = true;
        settings.SelectedPreset = "cn";
        Check(settings.Active().Id == "cn" && RoutingSettings.IsBuiltIn("ru") && !RoutingSettings.IsBuiltIn("custom-1"), "One preset acts at a time");

        // правка одного набора не трогает остальные и адреса источников
        settings.GeoipUrl = "https://example.com/custom.dat";
        var edited = settings.Active().WithRules("block", "# ads\ngeosite:category-ads-all\n");
        Check(settings.Active().Block == "" && edited.Block.Contains("category-ads-all") && edited.Direct == settings.Active().Direct, "WithRules returns a copy and keeps the other groups");
        Check(RoutingPreset.Lines(edited.Block).SequenceEqual(new[] { "geosite:category-ads-all" }), "Comments and blank lines are not rules");
        Check(RoutingSettings.Original("cn").SameRules(RoutingSettings.Original("cn")) && !RoutingSettings.Original("cn").SameRules(edited), "A built-in preset can be told from its edited version");
        Check(RoutingSettings.Original("cn").SameRules(RoutingSettings.Original("cn").WithRules("direct", "# comment\n\ngeoip:private\r\ngeoip:cn  \n")), "Only the rules matter when comparing");

        // адреса источников
        foreach (var good in new[] { "https://example.com/path/geoip.dat?version=1", "https://example.com:8443/db", RoutingSettings.DefaultGeoipUrl })
            Check(RoutingSettings.ValidSource(good), "Must be accepted: " + good);
        foreach (var bad in new[] { "http://example.com/db", "file:///db", "https://user:pass@example.com/db", "https://example.com:99999/db", "https://example.com/db#part", "", null,
                                    "ftp://example.com/x", "https://", "example.com/db", "https://example.com/a b", "https://example.com/" + new string('a', 2100) })
            Check(!RoutingSettings.ValidSource(bad), "Must be refused: " + bad);

        // испорченные данные приводятся к рабочему виду, встроенные наборы возвращаются на место
        var broken = new RoutingSettings
        {
            SelectedPreset = "gone", GeoipUrl = "http://insecure.example/geoip.dat", GeositeUrl = "",
            Presets = new List<RoutingPreset>
            {
                new RoutingPreset { Id = "custom-1", Name = " Mine ", Direct = null },
                new RoutingPreset { Id = "custom-1", Name = "Duplicate" },
                new RoutingPreset { Id = "" }, null,
                new RoutingPreset { Id = "ru", Direct = "geoip:ru" },
            },
        };
        broken.Normalize();
        Check(broken.Presets.Select(p => p.Id).SequenceEqual(new[] { "lan", "custom-1", "ru", "ir", "cn" }), "Built-ins are restored in place, duplicates and empty ids dropped: " + string.Join(",", broken.Presets.Select(p => p.Id)));
        Check(broken.Presets.Single(p => p.Id == "custom-1").Name == "Mine" && broken.Presets.Single(p => p.Id == "custom-1").Direct == "" && broken.Presets.Single(p => p.Id == "ru").Direct == "geoip:ru",
            "Names are trimmed, missing text becomes empty, the user's edits of a built-in are kept");
        Check(broken.SelectedPreset == "lan" && broken.GeoipUrl == RoutingSettings.DefaultGeoipUrl && broken.GeositeUrl == RoutingSettings.DefaultGeositeUrl, "A missing preset and unsafe sources fall back to the defaults");
        var longName = new RoutingSettings { Presets = new List<RoutingPreset> { new RoutingPreset { Id = "long", Name = new string('n', 200) } } };
        longName.Normalize();
        Check(longName.Presets.Single(p => p.Id == "long").Name.Length == 80, "A name longer than 80 characters is cut");

        // сохранение вместе с остальными настройками
        string path = Path.Combine(_data, "routing.json");
        var config = AppConfig.Load(path);
        Check(!config.Routing.Enabled && config.Routing.Presets.Count == 4, "A new config has routing off with the four presets");
        config.Routing.Enabled = true;
        config.Routing.SelectedPreset = "custom-9";
        config.Routing.Presets.Add(new RoutingPreset { Id = "custom-9", Name = "Work", Direct = "domain:corp.example", Block = "geosite:category-ads-all" });
        config.Routing.GeoipUrl = "https://example.com/geoip.dat";
        config.PerAppProxy = true;
        config.ProxyApps = new List<string> { @"C:\Tools\a.exe", @"c:\tools\A.exe", "  ", @"D:\b.exe" };
        config.Save();
        var loaded = AppConfig.Load(path);
        Check(loaded.Routing.Enabled && loaded.Routing.Active().Name == "Work" && loaded.Routing.Active().Block == "geosite:category-ads-all" && loaded.Routing.GeoipUrl == "https://example.com/geoip.dat" &&
              loaded.Routing.GeositeUrl == RoutingSettings.DefaultGeositeUrl, "Routing settings survive a restart");
        Check(loaded.PerAppProxy && loaded.ProxyApps.SequenceEqual(new[] { @"C:\Tools\a.exe", @"D:\b.exe" }), "The program list survives a restart without repeats (paths are compared ignoring case)");
        File.WriteAllText(path, "{\"Routing\":{\"Enabled\":true,\"SelectedPreset\":\"nope\",\"Presets\":[]}}");
        Check(AppConfig.Load(path).Routing.Presets.Count == 4 && AppConfig.Load(path).Routing.SelectedPreset == "lan", "A config with empty presets gets the defaults");
        File.WriteAllText(path, "{\"TestTimeoutSec\":20}");
        Check(AppConfig.Load(path).Routing.Presets.Count == 4 && !AppConfig.Load(path).PerAppProxy && AppConfig.Load(path).ProxyIpv6 && AppConfig.Load(path).ProxyDns == "1.1.1.1",
            "An old config without the new settings still loads with defaults");
        File.WriteAllText(path, "{\"StrategyCountry\":\"CN, ru ,xx\"}");
        Check(AppConfig.Load(path).StrategyCountry == "ru,cn", "A stored country filter is normalized when the settings load");
        File.WriteAllText(path, "{\"StrategyCountry\":\"nowhere\"}");
        Check(AppConfig.Load(path).StrategyCountry == "all", "An unknown stored country falls back to All");
        File.WriteAllText(path, "{\"TestTimeoutSec\":33,\"ProxyDns\":\"   \",\"Routing\":null,\"ProxyUri\":null,\"CustomEndpoints\":null}");
        var blank = AppConfig.Load(path);
        Check(blank.TestTimeoutSec == 33 && blank.ProxyDns == "1.1.1.1" && blank.Routing != null && blank.Routing.Presets.Count == 4 && blank.ProxyUri == "" && blank.CustomEndpoints == "",
            "Blank or missing connection settings fall back to the defaults without losing the rest of the settings");
        File.WriteAllText(path, "{\"ProxyDns\":\" 8.8.8.8 \"}");
        Check(AppConfig.Load(path).ProxyDns == "8.8.8.8", "The DNS server is trimmed");
    }

    static string Field(List<object> rules, int index, string name)
    {
        var rule = (Dictionary<string, object>)rules[index];
        return rule.ContainsKey(name) ? (string)rule[name] : null;
    }

    static List<string> Matcher(List<object> rules, int rule, string field)
    {
        var matchers = (List<object>)((Dictionary<string, object>)rules[rule])["rules"];
        var found = matchers.Cast<Dictionary<string, object>>().Where(m => m.ContainsKey(field)).ToList();
        return found.Count == 0 ? null : (List<string>)found[0][field];
    }

    static void TestRouteCompile()
    {
        string ip = TestGeoIpFile(), site = TestGeoSiteFile();
        Check(GeoDat.Info("geoip", ip).SequenceEqual(new[] { "inv", "other", "test" }) && GeoDat.Info("geosite", site).SequenceEqual(new[] { "cn", "test" }),
            "Category names are lowercased and listed alphabetically");

        // приоритет: блок, прокси, напрямую; у каждого поля своя ветка ИЛИ
        var rules = RouteRules.Compile(Preset("geoip:test", "full:allow.example.org", "geosite:test@ads"), ip, site, null);
        Check(rules.Count == 3 && Field(rules, 0, "action") == "reject" && Field(rules, 1, "outbound") == "proxy" && Field(rules, 2, "outbound") == "direct" &&
              Field(rules, 0, "type") == "logical" && Field(rules, 0, "mode") == "or", "Block comes first, then Proxy, then Direct");
        Check(Matcher(rules, 2, "ip_cidr").SequenceEqual(new[] { "10.0.0.0/8", "127.0.0.0/8", "2001:db8::/32" }), "Ranges are masked, deduplicated and sorted: " + string.Join(",", Matcher(rules, 2, "ip_cidr")));
        Check(Matcher(rules, 0, "domain_suffix").SequenceEqual(new[] { "example.org" }) && Matcher(rules, 0, "domain") == null, "An attribute selects only the entries that carry it");
        Check(Matcher(rules, 1, "domain").SequenceEqual(new[] { "allow.example.org" }), "A full name matches exactly");

        // типы записей GeoSite, отрицание атрибута, обратные диапазоны
        rules = RouteRules.Compile(Preset(proxy: "geosite:test"), ip, site, null);
        Check(Matcher(rules, 0, "domain").SequenceEqual(new[] { "full.example.org" }) && Matcher(rules, 0, "domain_regex").SequenceEqual(new[] { "^a[0-9]+\\.example\\.com$" }) &&
              Matcher(rules, 0, "domain_keyword").SequenceEqual(new[] { "tracker" }) && Matcher(rules, 0, "domain_suffix").SequenceEqual(new[] { "example.org", "plain.example.net" }),
            "All four kinds of GeoSite entries become their own matchers");
        rules = RouteRules.Compile(Preset(direct: "geosite:cn@!cn"), ip, site, null);
        Check(Matcher(rules, 0, "domain_suffix").SequenceEqual(new[] { "google.com" }), "@!attribute selects the entries without it");
        rules = RouteRules.Compile(Preset(direct: "geosite:cn@cn"), ip, site, null);
        Check(Matcher(rules, 0, "domain_suffix").SequenceEqual(new[] { "baidu.com", "qq.com" }), "@attribute selects the entries with it");
        rules = RouteRules.Compile(Preset(block: "geoip:inv\nexample.com"), ip, site, null);
        var matchers = (List<object>)((Dictionary<string, object>)rules[0])["rules"];
        Check(matchers.Count == 2 && ((Dictionary<string, object>)matchers[0]).ContainsKey("invert") && Matcher(rules, 0, "domain_suffix").SequenceEqual(new[] { "example.com" }),
            "An inverted GeoIP category is a separate inverted matcher");
        rules = RouteRules.Compile(Preset(direct: "full:b.example\nfull:a.example\nfull:b.example\n1.2.3.4\ngeoip:private"), null, null, null);
        Check(Matcher(rules, 0, "domain").SequenceEqual(new[] { "a.example", "b.example" }) && Matcher(rules, 0, "ip_cidr").Contains("1.2.3.4/32") &&
              Matcher(rules, 0, "ip_cidr").Contains("192.168.0.0/16") && Matcher(rules, 0, "ip_cidr").Contains("fe80::/10") && Matcher(rules, 0, "ip_cidr").Contains("224.0.0.0/4"),
            "geoip:private is built in and needs no database; domains and addresses stay separate branches");
        Check(RouteRules.Compile(Preset(), null, null, null).Count == 0, "No rules, no sing-box rules");

        // нет базы или категории - ошибка, а не пропуск правила
        foreach (var rule in new[] { "geoip:missing", "geosite:missing", "geosite:test@nothing" })
        {
            var e = Catch(() => RouteRules.Compile(Preset(block: rule), ip, site, null));
            Check(e is RouteRuleException && !string.IsNullOrWhiteSpace(e.Message), "A missing category is an error, not a skipped rule: " + rule + " -> " + e?.Message);
        }
        Check(Catch(() => RouteRules.Compile(Preset(direct: "geoip:ru"), null, null, null)) is RouteRuleException, "Without a database a country range is an error");
        Check(Catch(() => RouteRules.Compile(Preset(direct: "geosite:cn"), ip, null, null)) is RouteRuleException, "GeoSite always needs its own database");

        // встроенные диапазоны: пока нет своей базы; чужая база не заменяется молча встроенными
        var bundled = new Dictionary<string, List<string>> { ["ru"] = new List<string> { "5.8.0.0/16" } };
        rules = RouteRules.Compile(Preset(direct: "geoip:ru"), null, null, bundled);
        Check(Matcher(rules, 0, "ip_cidr").SequenceEqual(new[] { "5.8.0.0/16" }), "Bundled country ranges are used until a database is downloaded");
        Check(Catch(() => RouteRules.Compile(Preset(direct: "geoip:ru"), ip, null, bundled)) is RouteRuleException, "A downloaded database never falls back silently to the bundled ranges");

        // настоящие встроенные диапазоны
        var real = GeoData.BundledRanges();
        foreach (var country in new[] { "ru", "ir", "cn" })
        {
            Check(real.ContainsKey(country) && real[country].Count > 1000, country + ": the bundled ranges are present: " + (real.ContainsKey(country) ? real[country].Count : 0));
            Check(real[country].All(c => IpNet.TryParse(c, false, out var address, out int bits) && bits > 0 &&
                                         IpNet.TryParse(IpNet.Format(address, bits), false, out var again, out int sameBits) && sameBits == bits && again.SequenceEqual(address)),
                country + ": every bundled range is a valid network without host bits, and not the whole address space");
            Check(real[country].Any(c => c.Contains(":")) && real[country].Any(c => !c.Contains(":")), country + ": both IPv4 and IPv6 are covered");
        }
    }

    static void TestGeoDatValidation()
    {
        string Write(string name, byte[] data)
        {
            string path = Path.Combine(_data, name);
            File.WriteAllBytes(path, data);
            return path;
        }
        string Rejects(string kind, byte[] data, string name = "bad.dat") => Catch(() => GeoDat.Info(kind, Write(name, data)))?.GetType().Name;

        Check(GeoDat.Info("geoip", TestGeoIpFile()).Count == 3, "A good file is accepted");
        Check(Catch(() => GeoDat.Info("geoip", Path.Combine(_data, "missing.dat"))) is FileNotFoundException, "A missing file is reported as not downloaded");
        Check(Catch(() => GeoDat.Info("other", TestGeoIpFile())) is ArgumentException, "Only geoip and geosite exist");
        foreach (var data in new[]
        {
            new byte[0], Encoding.ASCII.GetBytes("<html>error</html>"), new byte[] { 0x0a, 0xff }, PbBytes(1, PbText(1, "empty")),
            PbJoin(PbBytes(1, GeoCategory("dup", false, CidrRecord("1.0.0.0", 8))), PbBytes(1, GeoCategory("DUP", false, CidrRecord("2.0.0.0", 8)))),
            PbBytes(1, GeoCategory("bad name!", false, CidrRecord("1.0.0.0", 8))),
            PbBytes(1, GeoCategory("badip", false, PbJoin(PbBytes(1, new byte[] { 1, 2, 3 }), PbInt(2, 32)))),
            PbBytes(1, GeoCategory("badlen", false, PbJoin(PbBytes(1, new byte[] { 1, 2, 3, 4 }), PbInt(2, 33)))),
            PbJoin(PbVarint(1 << 3 | 2), PbVarint(50), new byte[] { 1, 2 }), // длина больше остатка
            PbInt(1, 5), // запись базы не может быть числом
        })
            Check(Rejects("geoip", data) != null, "A corrupt database must be rejected: " + BitConverter.ToString(data.Take(12).ToArray()));
        Check(Rejects("geosite", PbBytes(1, GeoCategory("x", false, DomainRecord(9, "a.example")))) != null, "An unknown domain type is rejected");
        Check(Rejects("geosite", PbBytes(1, GeoCategory("x", false, DomainRecord(2, "")))) != null, "An empty domain is rejected");
        Check(Rejects("geosite", PbBytes(1, GeoCategory("x", false, DomainRecord(2, "a\nb.example")))) != null, "A domain with a line break is rejected");
        Check(Rejects("geosite", PbBytes(1, GeoCategory("x", false, PbJoin(PbInt(1, 2), PbBytes(2, new byte[] { 0xff, 0xfe }))))) != null, "A domain that is not UTF-8 is rejected");
        Check(Rejects("geosite", PbBytes(1, GeoCategory("x", false, PbJoin(DomainRecord(2, "a.example"), PbBytes(3, PbText(1, "bad attr!")))))) != null, "A bad attribute name is rejected");
        Check(Rejects("geosite", PbBytes(1, GeoCategory("x", false, DomainRecord(1, "(?P<name>a|b)\\.example")))) == null, "A regular expression is not judged by the .NET engine: sing-box checks the ones that are used");
        // обрезанный настоящий файл
        var good = File.ReadAllBytes(TestGeoSiteFile());
        Check(Rejects("geosite", good.Take(good.Length - 3).ToArray()) != null, "A truncated database is rejected");
    }

    // ------------------------------------------------------------------ загрузка баз

    sealed class ScriptedHttp : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond;
        public readonly List<string> Requests = new List<string>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri.ToString());
            return Task.FromResult(Respond(request));
        }
    }

    static HttpResponseMessage Ok(byte[] data) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    }

    static GeoData NewGeo(ScriptedHttp http)
    {
        var geo = new GeoData(Path.Combine(_data, "geo-" + Guid.NewGuid().ToString("N")));
        typeof(GeoData).GetField("HandlerFactory", PrivateInstance).SetValue(geo, (Func<HttpMessageHandler>)(() => new NonDisposing(http)));
        return geo;
    }

    /// <summary>HttpClient освобождает обработчик; тест держит один и тот же, чтобы читать его журнал запросов.</summary>
    sealed class NonDisposing : DelegatingHandler
    {
        public NonDisposing(HttpMessageHandler inner) : base(inner) { }
        protected override void Dispose(bool disposing) { }
    }

    static void TestGeoDataDownload()
    {
        const string A = "https://example.com/geoip.dat", B = "https://mirror.example/geoip.dat";
        byte[] valid = File.ReadAllBytes(TestGeoIpFile());
        var http = new ScriptedHttp { Respond = r => Ok(valid) };
        var geo = NewGeo(http);
        int changes = 0;
        geo.Changed += () => changes++;

        // успех: файл на месте, категории сосчитаны, ход загрузки виден
        Check(!geo.State("geoip", A).Exists, "Nothing is downloaded at first");
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        var state = geo.State("geoip", A);
        Check(state.Exists && state.Categories == 3 && state.Error == null && !state.Downloading && state.Bytes == valid.Length && changes > 0, "A good file is stored and counted");
        Check(File.ReadAllBytes(geo.DataFile("geoip", A)).SequenceEqual(valid) && !Directory.GetFiles(geo.Dir, "*.part").Any(), "The file on disk is the download; no partial files stay");

        // у другого адреса свои данные
        Check(!geo.State("geoip", B).Exists && !geo.State("geosite", A).Exists && GeoData.Key("geoip", A) != GeoData.Key("geoip", B), "Every source address has its own cache");

        // повреждённая замена не вытесняет исправную базу
        http.Respond = r => Ok(Encoding.ASCII.GetBytes("<html>login</html>"));
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        state = geo.State("geoip", A);
        Check(state.Error == L.T("geo.failed") && state.Exists && File.ReadAllBytes(geo.DataFile("geoip", A)).SequenceEqual(valid), "An invalid file keeps the last good database and reports a failure");
        Check(!Directory.GetFiles(geo.Dir, "*.part").Any(), "A failed download leaves no partial file");

        // успешное обновление заменяет базу
        byte[] newer = File.ReadAllBytes(WriteGeoFile("newer.dat", GeoCategory("only", false, CidrRecord("192.0.2.0", 24))));
        http.Respond = r => Ok(newer);
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        state = geo.State("geoip", A);
        Check(state.Error == null && state.Categories == 1 && File.ReadAllBytes(geo.DataFile("geoip", A)).SequenceEqual(newer), "A valid update replaces the database");

        // переходы только по HTTPS, не больше шести
        var chain = new ScriptedHttp();
        chain.Respond = r =>
        {
            string url = r.RequestUri.ToString();
            if (url.EndsWith("/start")) return Redirect("https://cdn.example/one");
            if (url.EndsWith("/one")) return Redirect("/two", HttpStatusCode.MovedPermanently);
            if (url.EndsWith("/two")) return Ok(valid);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        geo = NewGeo(chain);
        Sync(async () => { await geo.DownloadAsync("geoip", "https://example.com/start"); return 0; });
        Check(geo.State("geoip", "https://example.com/start").Exists && chain.Requests.SequenceEqual(new[] { "https://example.com/start", "https://cdn.example/one", "https://cdn.example/two" }),
            "Redirects are followed, relative ones too: " + string.Join(" > ", chain.Requests));

        foreach (var bad in new[] { "http://insecure.example/geoip.dat", "https://user:pass@example.com/geoip.dat", "https://example.com/geoip.dat#x" })
            Check(Catch(() => Sync(async () => { await geo.DownloadAsync("geoip", bad); return 0; })) is ArgumentException, "An unsafe address is refused before any request: " + bad);

        var downgrade = new ScriptedHttp { Respond = r => r.RequestUri.Scheme == "https" ? Redirect("http://plain.example/geoip.dat") : Ok(valid) };
        geo = NewGeo(downgrade);
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        Check(!geo.State("geoip", A).Exists && geo.State("geoip", A).Error != null && downgrade.Requests.Count == 1, "A redirect to plain HTTP is refused");

        var loop = new ScriptedHttp { Respond = r => Redirect("https://example.com/again") };
        geo = NewGeo(loop);
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        Check(!geo.State("geoip", A).Exists && loop.Requests.Count <= 7, "Endless redirects stop after six: " + loop.Requests.Count);

        // размер, обрыв, коды ответа
        var big = new ScriptedHttp { Respond = r => { var m = Ok(new byte[10]); m.Content.Headers.ContentLength = GeoData.MaxBytes + 1; return m; } };
        geo = NewGeo(big);
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        Check(!geo.State("geoip", A).Exists && geo.State("geoip", A).Error == L.T("geo.failed"), "A file over 64 MiB is refused by its declared size");
        var short_ = new ScriptedHttp { Respond = r => { var m = Ok(valid); m.Content.Headers.ContentLength = valid.Length + 100; return m; } };
        geo = NewGeo(short_);
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        Check(!geo.State("geoip", A).Exists, "A download shorter than announced is refused");
        var missing = new ScriptedHttp { Respond = r => new HttpResponseMessage(HttpStatusCode.NotFound) };
        geo = NewGeo(missing);
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        Check(!geo.State("geoip", A).Exists && geo.State("geoip", A).Error == L.T("geo.failed"), "An HTTP error is reported");
        var empty = new ScriptedHttp { Respond = r => Ok(new byte[0]) };
        geo = NewGeo(empty);
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        Check(!geo.State("geoip", A).Exists, "An empty download is refused");

        // адрес со служебными параметрами не попадает в сообщение
        var secret = "https://example.com/geoip.dat?token=SECRET123";
        geo = NewGeo(new ScriptedHttp { Respond = r => throw new HttpRequestException("failed " + r.RequestUri) });
        Sync(async () => { await geo.DownloadAsync("geoip", secret); return 0; });
        Check(geo.State("geoip", secret).Error == L.T("geo.failed") && !geo.State("geoip", secret).Error.Contains("SECRET"), "Network errors never echo the address");

        // отмена: прежняя база остаётся, сообщение говорит об отмене
        geo = NewGeo(new ScriptedHttp { Respond = r => Ok(valid) });
        Sync(async () => { await geo.DownloadAsync("geoip", A); return 0; });
        var stall = new ScriptedHttp { Respond = r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) } };
        typeof(GeoData).GetField("HandlerFactory", PrivateInstance).SetValue(geo, (Func<HttpMessageHandler>)(() => new NonDisposing(stall)));
        using (var cts = new CancellationTokenSource())
        {
            var running = Task.Run(() => geo.DownloadAsync("geoip", A, cts.Token));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!geo.AnyDownloading && clock.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            Check(geo.AnyDownloading && geo.State("geoip", A).Downloading, "A running download is visible");
            cts.Cancel();
            Check(running.Wait(10000), "Cancelling interrupts a stalled download");
        }
        state = geo.State("geoip", A);
        Check(state.Error == L.T("geo.cancelled") && state.Exists && !state.Downloading && state.Categories == 3, "A cancelled update keeps the previous database");
    }

    /// <summary>Поток, который отдаёт один байт и дальше зависает, пока его не освободят (как закрытое соединение).</summary>
    sealed class StallingStream : Stream
    {
        int _sent;
        readonly ManualResetEventSlim _released = new ManualResetEventSlim();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent == 0) { _sent = 1; buffer[offset] = 1; return 1; }
            _released.Wait();
            throw new ObjectDisposedException(nameof(StallingStream));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _released.Set();
            base.Dispose(disposing);
        }
    }
}
