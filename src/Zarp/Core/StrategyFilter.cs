using System;
using System.Collections.Generic;
using System.Linq;

namespace Zarp.Core
{
    /// <summary>
    /// Фильтр стратегий по странам. Значение в настройках: «all» или список через запятую («ru,cn»).
    /// Выбранные страны образуют объединение: стратегия подходит, если подходит хотя бы одной из них.
    /// </summary>
    public static class StrategyFilter
    {
        public const string All = "all";

        /// <summary>Допустимые значения в порядке показа: «all» и коды стран.</summary>
        public static readonly string[] Countries = { All, "ru", "ir", "cn" };

        /// <summary>Выбранные страны. Для «all» и пустого значения - пустое множество.</summary>
        public static HashSet<string> Selected(string value)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(value) || value.Trim().ToLowerInvariant() == All) return set;
            foreach (var part in value.Split(','))
            {
                string code = part.Trim().ToLowerInvariant();
                if (code != All && Countries.Contains(code)) set.Add(code);
            }
            return set;
        }

        /// <summary>Привести значение к каноническому виду: «all» или коды в порядке показа.</summary>
        public static string Normalize(string value) => Join(Selected(value));

        /// <summary>Включить или выключить страну. «all» сбрасывает фильтр; пустой выбор означает «all».</summary>
        public static string Toggle(string value, string country)
        {
            country = (country ?? "").Trim().ToLowerInvariant();
            if (country == All || !Countries.Contains(country)) return All;
            var current = Selected(value);
            if (!current.Remove(country)) current.Add(country);
            return Join(current);
        }

        static string Join(HashSet<string> selected)
        {
            string text = string.Join(",", Countries.Where(selected.Contains));
            return text.Length == 0 ? All : text;
        }

        /// <summary>
        /// Стратегия проходит фильтр. Собственный сервер (не WARP) фильтр не касается. Пока выбрана страна,
        /// стратегии без обхода (WARP как есть) и без метки страны исключаются: они остаются только при «all».
        /// </summary>
        public static bool Matches(Strategy strategy, string value) => Matches(strategy, Selected(value));

        static bool Matches(Strategy strategy, HashSet<string> selected) =>
            strategy.IsProxy || selected.Count == 0 || (strategy.UsesZapret && strategy.Countries.Any(selected.Contains));

        public static List<Strategy> Apply(IEnumerable<Strategy> strategies, string value)
        {
            var selected = Selected(value);
            return strategies.Where(s => Matches(s, selected)).ToList();
        }

        /// <summary>Коды стран из текста вида «ru, cn». Неизвестные коды и «all» пропускаются.</summary>
        public static string[] ParseCountries(string text) =>
            Countries.Where(Selected(text ?? "").Contains).Where(c => c != All).ToArray();
    }
}
