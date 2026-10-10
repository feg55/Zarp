using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Zarp.Core
{
    /// <summary>
    /// Собственные эндпоинты WARP: по одному адресу IP:порт или [IPv6]:порт на строку.
    /// Только числовые адреса: проверка не должна обращаться к DNS. Пустые строки и комментарии после # пропускаются.
    /// </summary>
    public static class EndpointParser
    {
        /// <summary>Разобрать текст. Неверная строка - FormatException; повторы убираются, порядок сохраняется.</summary>
        public static List<string> Parse(string text)
        {
            var result = new List<string>();
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                int hash = raw.IndexOf('#');
                string line = (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
                if (line.Length == 0) continue;
                string endpoint = ParseLine(line);
                if (!result.Contains(endpoint)) result.Add(endpoint);
            }
            return result;
        }

        public static bool TryParse(string text, out List<string> endpoints)
        {
            try { endpoints = Parse(text); return true; }
            catch (FormatException) { endpoints = new List<string>(); return false; }
        }

        /// <summary>IP-адреса эндпоинтов (без портов) - для фильтра перехвата.</summary>
        public static List<string> Addresses(IEnumerable<string> endpoints) =>
            endpoints.Select(e => e.StartsWith("[") ? e.Substring(1, e.IndexOf(']') - 1) : e.Substring(0, e.LastIndexOf(':')))
                .Distinct().ToList();

        static string ParseLine(string line)
        {
            bool v6 = line.StartsWith("[");
            string host, portText;
            if (v6)
            {
                int close = line.IndexOf(']');
                if (close < 0 || close + 1 >= line.Length || line[close + 1] != ':') throw Invalid();
                host = line.Substring(1, close - 1);
                portText = line.Substring(close + 2);
            }
            else
            {
                int colon = line.IndexOf(':');
                host = colon < 0 ? line : line.Substring(0, colon);
                portText = colon < 0 ? "" : line.Substring(colon + 1);
            }
            if (portText.Length == 0 || portText.Length > 5 || !portText.All(c => c >= '0' && c <= '9')) throw Invalid();
            int port = int.Parse(portText, CultureInfo.InvariantCulture);
            if (port < 1 || port > 65535) throw Invalid();

            if (v6)
            {
                if (!host.Contains(":") || !host.All(c => Uri.IsHexDigit(c) || c == ':' || c == '.')) throw Invalid();
                if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) throw Invalid();
                return "[" + host + "]:" + port.ToString(CultureInfo.InvariantCulture);
            }
            var parts = host.Split('.');
            if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.Length <= 3 && p.All(c => c >= '0' && c <= '9')
                    && int.Parse(p, CultureInfo.InvariantCulture) <= 255 && (p == "0" || p[0] != '0')))
                throw Invalid();
            return host + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        static FormatException Invalid() => new FormatException("endpoint.invalid");
    }
}
