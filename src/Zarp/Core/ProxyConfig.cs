using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Zarp.Core
{
    /// <summary>Параметры одного запуска sing-box: проверка сервера или подключение с виртуальным адаптером.</summary>
    public sealed class ProxySession
    {
        public ProxyProfile Profile;
        /// <summary>Адрес сервера, разрешённый заранее: до подключения системный DNS ещё доступен, после - уже нет.</summary>
        public string Address;
        /// <summary>Порт проверочного входа на 127.0.0.1 и его учётные данные: чужие программы им не воспользуются.</summary>
        public int Port;
        public string User;
        public string Password;
        /// <summary>true: виртуальный адаптер и маршрутизация всего трафика; false: только проверочный вход.</summary>
        public bool Tun;
        public bool Ipv6 = true;
        /// <summary>DNS-серверы через запятую; используется первый. Запросы идут через прокси.</summary>
        public string Dns = "1.1.1.1";
        /// <summary>Правила пользователя в порядке «Блок, Прокси, Напрямую» (см. RouteRules.Compile). Пусто - правил нет.</summary>
        public List<object> Rules = new List<object>();
        /// <summary>Только эти программы идут через прокси (полные пути exe); null - все.</summary>
        public List<string> AppPaths;
        /// <summary>
        /// Путь к exe самого Zarp. При выборе программ его трафик идёт через сервер, как бы ни был устроен выбор: так проверка
        /// «адаптер пропускает трафик» не зависит от правил и от того, доступен ли Cloudflare напрямую.
        /// </summary>
        public string OwnPath;
        /// <summary>
        /// Только для тестов: правила и выбор программ действуют и на проверочный вход (без адаптера), чтобы их можно было
        /// проверить настоящим sing-box без изменения сети. В программе проверка всегда идёт через сервер.
        /// </summary>
        public bool RulesOnProbe;
    }

    /// <summary>Сборка конфигурации sing-box 1.13 из параметров сессии. Конфигурацию целиком составляет Zarp, чужая не принимается.</summary>
    public static class ProxyConfig
    {
        const string Probe = "probe-in";

        /// <summary>Ровно как QuoteMeta в Go: экранирует спецсимволы RE2 и ничего лишнего (пробел экранировать нельзя).</summary>
        public static string QuoteMeta(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text)
            {
                if ("\\.+*?()|[]{}^$".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Регулярное выражение для пути программы: без учёта регистра и целиком.</summary>
        public static string AppPattern(string path) => "(?i)^" + QuoteMeta(path) + "$";

        static Dictionary<string, object> Rule(params object[] pairs)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }

        public static string Build(ProxySession s)
        {
            string dns = (s.Dns ?? "").Split(',').Select(x => x.Trim()).FirstOrDefault(x => IPAddress.TryParse(x, out _)) ?? "1.1.1.1";
            bool userRules = s.Rules != null && s.Rules.Count > 0;
            string strategy = s.Ipv6 ? "prefer_ipv4" : "ipv4_only";

            var inbounds = new List<object>();
            if (s.Tun)
            {
                var addresses = new List<string> { "172.19.0.1/30", "fdfe:dcba:9876::1/126" };
                // Стек gVisor целиком в процессе sing-box: ему не нужны слушающие сокеты на адаптере и правила брандмауэра.
                inbounds.Add(Rule("type", "tun", "tag", "tun-in", "interface_name", SingBox.TunName,
                    "address", addresses, "auto_route", true, "strict_route", true, "stack", "gvisor"));
            }
            // только SOCKS5 с паролем: вход HTTP-прокси при ошибке входа закрывает соединение сбросом (см. ProbeClient)
            inbounds.Add(Rule("type", "socks", "tag", Probe, "listen", "127.0.0.1", "listen_port", s.Port,
                "users", new List<object> { Rule("username", s.User, "password", s.Password) }));

            var rules = new List<object>();
            // проверка соединения всегда идёт через сервер, что бы ни говорили правила
            if (!s.RulesOnProbe) rules.Add(Rule("inbound", new List<object> { Probe }, "action", "route", "outbound", "proxy"));
            if (s.Tun || s.RulesOnProbe)
            {
                if (s.AppPaths != null && !string.IsNullOrEmpty(s.OwnPath))
                    rules.Add(Rule("process_path_regex", new List<string> { AppPattern(s.OwnPath) }, "action", "route", "outbound", "proxy"));
                if (s.AppPaths != null)
                {
                    // всё, что не от выбранных программ, идёт мимо прокси и дальше правил не проходит
                    rules.Add(Rule("process_path_regex", s.AppPaths.Select(AppPattern).ToList(), "invert", true,
                        "action", "route", "outbound", "direct"));
                }
                // без IPv6 в туннеле его трафик не должен утекать мимо: отклоняется, программы переходят на IPv4
                if (!s.Ipv6) rules.Add(Rule("ip_version", 6, "action", "reject"));
                if (userRules)
                    rules.Add(Rule("action", "sniff", "sniffer", new List<object> { "http", "tls", "quic" }, "timeout", "300ms"));
                rules.Add(Rule("port", 53, "action", "hijack-dns"));
                if (userRules) rules.Add(Rule("action", "resolve", "strategy", strategy));
                if (userRules) rules.AddRange(s.Rules);
                // Локальная сеть остаётся локальной, если правила не сказали иное (они стоят выше).
                rules.Add(Rule("ip_is_private", true, "action", "route", "outbound", "direct"));
            }

            var config = Rule(
                "log", Rule("level", "warn", "timestamp", true),
                "dns", Rule(
                    "servers", new List<object>
                    {
                        Rule("type", "local", "tag", "bootstrap"),
                        Rule("type", "udp", "tag", "remote", "server", dns, "detour", "proxy"),
                    },
                    "final", "remote", "strategy", strategy, "reverse_mapping", userRules),
                "inbounds", inbounds,
                "outbounds", new List<object>
                {
                    s.Profile.BuildOutbound(s.Address),
                    Rule("type", "direct", "tag", "direct", "domain_resolver", "bootstrap"),
                },
                "route", Rule("auto_detect_interface", true, "default_domain_resolver", "remote", "final", "proxy", "rules", rules));
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(config);
        }
    }
}
