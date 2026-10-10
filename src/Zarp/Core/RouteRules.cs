using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Zarp.Core
{
    /// <summary>Ошибка в правилах маршрутизации или в данных, на которые они ссылаются. Текст - для показа пользователю.</summary>
    public sealed class RouteRuleException : Exception
    {
        public RouteRuleException(string message) : base(message) { }
    }

    /// <summary>Одно правило после разбора: вид (ip_cidr, domain, domain_suffix, domain_keyword, domain_regex, geoip, geosite), значение и атрибут GeoSite.</summary>
    public sealed class RouteToken
    {
        public string Kind, Value, Attr;
    }

    /// <summary>
    /// Правила маршрутизации в синтаксисе V2Ray/Xray и их перевод в правила sing-box.
    /// Приоритет при пересечении: «Блок», «Прокси», «Напрямую». Неверное правило, отсутствующая база или неизвестная категория -
    /// ошибка, а не молчаливый пропуск: пропущенное правило могло бы отправить трафик не туда.
    /// </summary>
    public static class RouteRules
    {
        // Какие адреса считаются локальными для geoip:private (то же, что в клиентах V2Ray и в Android-версии Zarp).
        static readonly string[] PrivateRanges =
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16",
            "224.0.0.0/4", "240.0.0.0/4", "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8",
        };

        const int MaxRulesPerGroup = 10000;

        static RouteRuleException Bad(string message) => new RouteRuleException(message);

        /// <summary>Разобрать одну строку правила.</summary>
        public static RouteToken ParseToken(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0 || s.Length > 4096) throw Bad("invalid rule length");
            if (IpNet.TryParse(s, true, out var ip, out int prefix))
                return new RouteToken { Kind = "ip_cidr", Value = IpNet.Format(ip, prefix) };

            string kind, value;
            int colon = s.IndexOf(':');
            if (colon < 0)
            {
                // «999.1.2.3» - не домен, а неверный адрес
                if (s.All(c => (c >= '0' && c <= '9') || c == '.')) throw Bad("invalid IP address");
                kind = "domain";
                value = s;
            }
            else
            {
                kind = s.Substring(0, colon);
                value = s.Substring(colon + 1);
            }
            kind = kind.ToLowerInvariant();
            switch (kind)
            {
                case "geoip":
                case "geosite":
                {
                    string lowered = value.ToLowerInvariant();
                    int at = lowered.IndexOf('@');
                    string name = at < 0 ? lowered : lowered.Substring(0, at);
                    string attr = at < 0 ? "" : lowered.Substring(at + 1);
                    // атрибут бывает только у GeoSite; «!» перед ним означает «записи без этого атрибута»
                    string attrName = attr.StartsWith("!") ? attr.Substring(1) : attr;
                    bool attrOk = attr.Length == 0 || (kind == "geosite" && GeoDat.ValidTag(attrName));
                    if (!GeoDat.ValidTag(name) || !attrOk || value.EndsWith("@")) throw Bad("invalid geo category");
                    return new RouteToken { Kind = kind, Value = name, Attr = attr };
                }
                case "regexp":
                    if (value.Length == 0) throw Bad("empty regex");
                    CheckRegex(value);
                    return new RouteToken { Kind = "domain_regex", Value = value };
                case "keyword":
                    if (value.Length == 0 || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw Bad("invalid keyword");
                    return new RouteToken { Kind = "domain_keyword", Value = value };
                case "domain":
                case "full":
                    value = value.ToLowerInvariant().TrimEnd('.');
                    if (value.Length == 0 || value.Length > 253) throw Bad("invalid domain");
                    foreach (var label in value.Split('.'))
                    {
                        if (label.Length == 0 || label.Length > 63 || label.StartsWith("-") || label.EndsWith("-")) throw Bad("invalid domain");
                        if (!label.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))
                            throw Bad("use an ASCII or punycode domain");
                    }
                    return new RouteToken { Kind = kind == "full" ? "domain" : "domain_suffix", Value = value };
            }
            throw Bad("unknown rule type or invalid IP/CIDR");
        }

        /// <summary>
        /// Выражения RE2 (его использует sing-box) - подмножество того, что понимает .NET: просмотр вперёд и назад, обратные
        /// ссылки и атомарные группы в RE2 недопустимы. Окончательная проверка - `sing-box check` перед подключением.
        /// </summary>
        static void CheckRegex(string pattern)
        {
            if (Regex.IsMatch(pattern, @"\(\?[=!>]|\(\?<[=!]|\(\?\(|\\[1-9]|\\k<"))
                throw Bad("invalid regex (RE2)");
            try { new Regex(pattern.Replace("(?P<", "(?<")); }
            catch (ArgumentException) { throw Bad("invalid regex (RE2)"); }
        }

        static string GroupName(string action) => action == "direct" ? L.T("routing.direct") : action == "proxy" ? L.T("routing.proxy") : L.T("routing.block");

        /// <summary>Разобрать все три группы набора. Ошибка называет группу и номер строки.</summary>
        public static Dictionary<string, List<RouteToken>> Tokens(RoutingPreset preset)
        {
            var result = new Dictionary<string, List<RouteToken>>();
            long total = (preset.Direct ?? "").Length + (preset.Proxy ?? "").Length + (preset.Block ?? "").Length;
            if (total > 1 << 20) throw Bad("too many rules");
            foreach (var action in RoutingPreset.Actions)
            {
                var lines = (preset.Text(action) ?? "").Replace("\r", "").Split('\n');
                var list = new List<RouteToken>();
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (list.Count >= MaxRulesPerGroup) throw Bad("too many rules");
                    // номер строки - тот, что видит пользователь в редакторе, вместе с комментариями и пустыми строками
                    try { list.Add(ParseToken(line)); }
                    catch (RouteRuleException e) { throw Bad(GroupName(action) + ", " + L.T("routing.line", i + 1) + ": " + e.Message); }
                }
                result[action] = list;
            }
            return result;
        }

        /// <summary>Проверить синтаксис, не обращаясь к базам.</summary>
        public static void Validate(RoutingPreset preset) => Tokens(preset);

        /// <summary>
        /// Перевести набор в правила sing-box по порядку «Блок, Прокси, Напрямую». Из баз .dat разворачиваются только упомянутые
        /// категории. bundled - встроенные диапазоны стран: они используются, пока не скачана своя база GeoIP (путь пуст).
        /// </summary>
        public static List<object> Compile(RoutingPreset preset, string geoipPath, string geositePath, IDictionary<string, List<string>> bundled)
        {
            var tokens = Tokens(preset);
            var loaded = new Dictionary<string, GeoDatabase>();
            GeoDatabase Load(string kind)
            {
                if (loaded.TryGetValue(kind, out var known)) return known;
                string path = kind == "geoip" ? geoipPath : geositePath;
                if (string.IsNullOrEmpty(path)) throw Bad(kind + ": database is not downloaded");
                try { return loaded[kind] = GeoDat.Read(path, kind); }
                catch (Exception e) when (e is InvalidDataException || e is FileNotFoundException) { throw Bad(kind + ": " + e.Message.Replace(kind + ": ", "")); }
            }

            string[] fields = { "domain", "domain_suffix", "domain_keyword", "domain_regex", "ip_cidr" };
            var compiled = new List<object>();
            foreach (var action in new[] { "block", "proxy", "direct" })
            {
                var values = fields.ToDictionary(f => f, f => new List<string>());
                var matchers = new List<object>();
                foreach (var token in tokens[action])
                {
                    if (token.Kind == "geoip")
                    {
                        if (token.Value == "private") { values["ip_cidr"].AddRange(PrivateRanges); continue; }
                        if (string.IsNullOrEmpty(geoipPath) && bundled != null && bundled.TryGetValue(token.Value, out var own) && own.Count > 0)
                        {
                            values["ip_cidr"].AddRange(own);
                            continue;
                        }
                        var db = Load("geoip");
                        if (!db.Entries.TryGetValue(token.Value, out var entry)) throw Bad("geoip:" + token.Value + ": category not found");
                        var cidrs = new List<string>(entry.Records.Count);
                        try { foreach (var record in entry.Records) cidrs.Add(GeoDat.Cidr(record)); }
                        catch (InvalidDataException e) { throw Bad("geoip: " + e.Message); }
                        if (entry.Inverse) matchers.Add(new Dictionary<string, object> { ["ip_cidr"] = cidrs, ["invert"] = true });
                        else values["ip_cidr"].AddRange(cidrs);
                    }
                    else if (token.Kind == "geosite")
                    {
                        var db = Load("geosite");
                        if (!db.Entries.TryGetValue(token.Value, out var entry)) throw Bad("geosite:" + token.Value + ": category not found");
                        int matched = 0;
                        try
                        {
                            foreach (var record in entry.Records)
                            {
                                var d = GeoDat.ParseDomain(record);
                                if (token.Attr.Length > 0)
                                {
                                    bool negate = token.Attr.StartsWith("!");
                                    d.Attributes.TryGetValue(token.Attr.TrimStart('!'), out bool present);
                                    if (present == negate) continue;
                                }
                                values[new[] { "domain_keyword", "domain_regex", "domain_suffix", "domain" }[d.Type]].Add(d.Value);
                                matched++;
                            }
                        }
                        catch (InvalidDataException e) { throw Bad("geosite: " + e.Message); }
                        if (matched == 0) throw Bad("geosite:" + token.Value + (token.Attr.Length > 0 ? "@" + token.Attr : "") + ": no matching domains");
                    }
                    else values[token.Kind].Add(token.Value);
                }
                // каждое поле - отдельная ветка ИЛИ: условия по IP и по домену не превращаются в И
                foreach (var field in fields)
                {
                    var list = values[field];
                    if (list.Count == 0) continue;
                    list.Sort(StringComparer.Ordinal);
                    var unique = new List<string>(list.Count);
                    foreach (var value in list)
                        if (unique.Count == 0 || unique[unique.Count - 1] != value) unique.Add(value);
                    matchers.Add(new Dictionary<string, object> { [field] = unique });
                }
                if (matchers.Count == 0) continue;
                var rule = new Dictionary<string, object> { ["type"] = "logical", ["mode"] = "or", ["rules"] = matchers };
                if (action == "block") rule["action"] = "reject";
                else
                {
                    rule["action"] = "route";
                    rule["outbound"] = action;
                }
                compiled.Add(rule);
            }
            return compiled;
        }
    }
}
