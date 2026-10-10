using System;
using System.Collections.Generic;
using System.Linq;

namespace Zarp.Core
{
    /// <summary>
    /// Набор правил маршрутизации: три группы по одному правилу в строке («Напрямую», «Прокси», «Блок»).
    /// У встроенных наборов Name пуст: имя берётся из перевода по Id (lan, ru, ir, cn).
    /// </summary>
    public sealed class RoutingPreset
    {
        public const string Direct_ = "direct", Proxy_ = "proxy", Block_ = "block";
        public static readonly string[] Actions = { Direct_, Proxy_, Block_ };

        public string Id { get; set; }
        public string Name { get; set; } = "";
        public string Direct { get; set; } = "";
        public string Proxy { get; set; } = "";
        public string Block { get; set; } = "";

        public string Text(string action) => action == Direct_ ? Direct : action == Proxy_ ? Proxy : Block;

        public RoutingPreset WithRules(string action, string text)
        {
            var copy = Clone();
            if (action == Direct_) copy.Direct = text ?? "";
            else if (action == Proxy_) copy.Proxy = text ?? "";
            else copy.Block = text ?? "";
            return copy;
        }

        public RoutingPreset Clone() => new RoutingPreset { Id = Id, Name = Name, Direct = Direct, Proxy = Proxy, Block = Block };

        /// <summary>Правила без пустых строк и комментариев (строки, начинающиеся с #).</summary>
        public static List<string> Lines(string text) =>
            (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();

        /// <summary>Совпадают ли правила (сравниваются именно правила, а не пробелы и комментарии).</summary>
        public bool SameRules(RoutingPreset other) =>
            Lines(Direct).SequenceEqual(Lines(other.Direct)) && Lines(Proxy).SequenceEqual(Lines(other.Proxy)) &&
            Lines(Block).SequenceEqual(Lines(other.Block)) && (Name ?? "") == (other.Name ?? "");
    }

    /// <summary>
    /// Настройки маршрутизации: включена ли она, какой набор действует (один), сами наборы и адреса баз GeoIP и GeoSite.
    /// Приоритет правил: «Блок», затем «Прокси», затем «Напрямую»; трафик без совпадений идёт через сервер.
    /// </summary>
    public sealed class RoutingSettings
    {
        public const string DefaultGeoipUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat";
        public const string DefaultGeositeUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat";

        public bool Enabled { get; set; } = false;
        public string SelectedPreset { get; set; } = "lan";
        public List<RoutingPreset> Presets { get; set; } = Defaults();
        public string GeoipUrl { get; set; } = DefaultGeoipUrl;
        public string GeositeUrl { get; set; } = DefaultGeositeUrl;

        /// <summary>Набор, который действует сейчас: null, если маршрутизация выключена.</summary>
        public RoutingPreset Active() => Enabled ? Presets.FirstOrDefault(p => p.Id == SelectedPreset) : null;

        /// <summary>Встроенные наборы: локальная сеть и страны. Страновые отправляют напрямую локальные адреса и IP страны.</summary>
        public static List<RoutingPreset> Defaults() => new List<RoutingPreset>
        {
            new RoutingPreset { Id = "lan", Direct = "geoip:private" },
            new RoutingPreset { Id = "ru", Direct = "geoip:private\ngeoip:ru" },
            new RoutingPreset { Id = "ir", Direct = "geoip:private\ngeoip:ir" },
            new RoutingPreset { Id = "cn", Direct = "geoip:private\ngeoip:cn" },
        };

        public static bool IsBuiltIn(string id) => Defaults().Any(p => p.Id == id);

        /// <summary>Исходные правила встроенного набора или null для своего.</summary>
        public static RoutingPreset Original(string id) => Defaults().FirstOrDefault(p => p.Id == id);

        /// <summary>
        /// Адрес источника: только HTTPS, без имени и пароля, без фрагмента, не длиннее 2048 знаков, порт 1-65535.
        /// Так загрузка не уйдёт на открытое соединение и не раскроет учётные данные.
        /// </summary>
        public static bool ValidSource(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 2048) return false;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.UserInfo) &&
                   value.IndexOf('#') < 0 && (uri.IsDefaultPort || (uri.Port >= 1 && uri.Port <= 65535)) && value.IndexOf(' ') < 0;
        }

        /// <summary>Привести испорченные данные к рабочему виду: у наборов есть уникальные Id, выбранный набор существует.</summary>
        public void Normalize()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var presets = new List<RoutingPreset>();
            foreach (var p in Presets ?? new List<RoutingPreset>())
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Id) || !seen.Add(p.Id)) continue;
                p.Name = (p.Name ?? "").Trim();
                if (p.Name.Length > 80) p.Name = p.Name.Substring(0, 80);
                p.Direct = p.Direct ?? "";
                p.Proxy = p.Proxy ?? "";
                p.Block = p.Block ?? "";
                presets.Add(p);
            }
            // встроенные наборы всегда на месте: пользователь может править их правила, но не удалять их
            // недостающий встроенный набор встаёт после ближайшего предыдущего встроенного (или в начало списка)
            var defaults = Defaults();
            for (int i = 0; i < defaults.Count; i++)
            {
                if (!seen.Add(defaults[i].Id)) continue;
                int at = 0;
                for (int j = i - 1; j >= 0 && at == 0; j--)
                {
                    int found = presets.FindIndex(p => p.Id == defaults[j].Id);
                    if (found >= 0) at = found + 1;
                }
                presets.Insert(at, defaults[i]);
            }
            Presets = presets;
            if (Presets.All(p => p.Id != SelectedPreset)) SelectedPreset = "lan";
            if (!ValidSource(GeoipUrl)) GeoipUrl = DefaultGeoipUrl;
            if (!ValidSource(GeositeUrl)) GeositeUrl = DefaultGeositeUrl;
        }
    }
}
