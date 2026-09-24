using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Zarp.Core
{
    /// <summary>Каким протоколом WARP поднимает туннель.</summary>
    public enum WarpTransport
    {
        /// <summary>MASQUE поверх HTTP/3 (QUIC, UDP).</summary>
        MasqueH3,
        /// <summary>MASQUE поверх HTTP/2 (TLS, TCP).</summary>
        MasqueH2,
        /// <summary>Классический WireGuard (UDP).</summary>
        WireGuard,
    }

    /// <summary>
    /// Стратегия = протокол туннеля WARP + профиль winws2 (zapret2).
    /// Пустой Args означает «без zapret» - WARP подключается напрямую.
    /// </summary>
    public sealed class Strategy
    {
        public string Id;
        /// <summary>Ключ перевода для имени (у встроенных стратегий, имя которых надо переводить).</summary>
        public string NameKey;
        string _name;
        /// <summary>Имя для показа: переведённое по NameKey или заданное как есть.</summary>
        public string Name { get => NameKey != null ? L.T(NameKey) : _name; set => _name = value; }
        public WarpTransport Transport;
        public string Args;
        public bool Custom;

        public bool UsesZapret => !string.IsNullOrWhiteSpace(Args);

        public Strategy Clone() => new Strategy
        {
            Id = Id, NameKey = NameKey, _name = _name, Transport = Transport, Args = Args, Custom = Custom,
        };

        public static string TransportTitle(WarpTransport t)
        {
            switch (t)
            {
                case WarpTransport.MasqueH3: return "MASQUE / HTTP3";
                case WarpTransport.MasqueH2: return "MASQUE / HTTP2";
                default: return "WireGuard";
            }
        }

        public static bool TryParseTransport(string s, out WarpTransport t)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "h3": case "masque": case "masque-h3": t = WarpTransport.MasqueH3; return true;
                case "h2": case "masque-h2": t = WarpTransport.MasqueH2; return true;
                case "wg": case "wireguard": t = WarpTransport.WireGuard; return true;
            }
            t = WarpTransport.MasqueH3;
            return false;
        }

        public override string ToString() => Name;
    }

    /// <summary>Встроенный набор стратегий + пользовательские из strategies.txt.</summary>
    public static class StrategyCatalog
    {
        // Стратегии именно под рукопожатие WARP. Все атаки - фейки перед первым пакетом туннеля:
        //  * MASQUE/HTTP3: QUIC Initial с SNI consumer-masque.cloudflareclient.com (post-quantum, 2 пакета);
        //  * WireGuard: handshake initiation 148 байт с узнаваемой сигнатурой;
        //  * MASQUE/HTTP2: TLS ClientHello по TCP (фолбэк клиента WARP).
        // Фейк должен выглядеть как разрешённый трафик: реальные пакеты google/vk/STUN,
        // а не нули - «пустые» фейки DPI отбрасывает (проверено: fake_default_quic не проходит).
        // Порядок важен: поиск идёт сверху вниз, поэтому наиболее вероятные варианты стоят первыми.
        // Имена технические и одинаковые на всех языках: fake, ttl, badsum - термины zapret из аргументов.
        static readonly Strategy[] BuiltIn =
        {
            // ---------- MASQUE / HTTP3 (протокол по умолчанию в клиенте WARP) ----------
            S("warp-q-google6",   "WARP QUIC: fake google ×6",            WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=6"),
            S("warp-q-google3",   "WARP QUIC: fake google ×3",            WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=3"),
            S("warp-q-vk6",       "WARP QUIC: fake vk ×6",                WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_vk:repeats=6"),
            S("warp-q-google-vk", "WARP QUIC: fakes google + vk",         WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=3 --lua-desync=fake:blob=quic_vk:repeats=3"),
            S("warp-q-google10",  "WARP QUIC: fake google ×10",           WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=10"),
            S("warp-q-google-ttl","WARP QUIC: fake google ttl=4 ×6",      WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:ip_ttl=4:ip6_ttl=4:repeats=6"),
            S("warp-q-vk-ttl",    "WARP QUIC: fake vk ttl=4 ×6",          WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_vk:ip_ttl=4:ip6_ttl=4:repeats=6"),
            S("warp-q-google-bad","WARP QUIC: fake google badsum ×6",     WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:badsum:repeats=6"),

            // ---------- WireGuard ----------
            S("warp-wg-google6",  "WARP WireGuard: fake QUIC google ×6",  WarpTransport.WireGuard,
              "--payload=wireguard_initiation --lua-desync=fake:blob=quic_google:repeats=6"),
            S("warp-wg-stun",     "WARP WireGuard: fake STUN ×6",         WarpTransport.WireGuard,
              "--payload=wireguard_initiation --lua-desync=fake:blob=stun_fake:repeats=6"),
            S("warp-wg-vk10",     "WARP WireGuard: fake QUIC vk ×10",     WarpTransport.WireGuard,
              "--payload=wireguard_initiation --lua-desync=fake:blob=quic_vk:repeats=10"),
            S("warp-wg-google-ttl","WARP WireGuard: fake google ttl=4",   WarpTransport.WireGuard,
              "--payload=wireguard_initiation --lua-desync=fake:blob=quic_google:ip_ttl=4:ip6_ttl=4:repeats=6"),

            // ---------- MASQUE / HTTP2 (TLS по TCP) ----------
            S("warp-t-google-md5","WARP TLS: fake google md5 + split",    WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=fake:blob=tls_google:tcp_md5:repeats=6 --lua-desync=multisplit:pos=1,midsld"),
            S("warp-t-seqovl",    "WARP TLS: seqovl google",              WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google"),
            S("warp-t-vk-seq",    "WARP TLS: fake vk badseq + disorder",  WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=fake:blob=tls_vk:tcp_seq=-3000:repeats=6 --lua-desync=multidisorder:pos=1,midsld"),
            S("warp-t-hostfake",  "WARP TLS: hostfakesplit vk.com",       WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=hostfakesplit:host=vk.com:tcp_md5"),

            // ---------- Контроль: вдруг WARP в этой сети и так работает ----------
            new Strategy { Id = "direct", NameKey = "strategy.direct", Transport = WarpTransport.MasqueH3, Args = "" },
        };

        static Strategy S(string id, string name, WarpTransport t, string args) =>
            new Strategy { Id = id, Name = name, Transport = t, Args = args };

        public const string CustomFileName = "strategies.txt";

        /// <summary>Все стратегии: встроенные + пользовательские.</summary>
        public static List<Strategy> Load(string dataDir)
        {
            var list = BuiltIn.Select(s => s.Clone()).ToList();
            string path = Path.Combine(dataDir, CustomFileName);
            try
            {
                if (!File.Exists(path))
                    File.WriteAllText(path, CustomTemplate, new UTF8Encoding(false));
                list.AddRange(ParseCustom(File.ReadAllLines(path, Encoding.UTF8)));
            }
            catch (Exception e)
            {
                Log.Write(L.T("log.customReadFailed", CustomFileName, e.Message));
            }
            return list;
        }

        static IEnumerable<Strategy> ParseCustom(string[] lines)
        {
            int n = 0;
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { '|' }, 3);
                if (parts.Length < 3 || !Strategy.TryParseTransport(parts[1], out var t))
                {
                    Log.Write(L.T("log.customSkipped", CustomFileName, line));
                    continue;
                }
                n++;
                yield return new Strategy
                {
                    Id = "custom-" + parts[0].Trim().ToLowerInvariant().Replace(' ', '-'),
                    Name = "★ " + parts[0].Trim(),
                    Transport = t,
                    Args = parts[2].Trim(),
                    Custom = true,
                };
            }
        }

        // Файл настройки для опытных пользователей: синтаксис технический, поэтому комментарии на английском.
        const string CustomTemplate =
@"# Custom Zarp strategies. One line = one strategy:
#   Name | transport | winws2 profile arguments
# transport: h3 (MASQUE/QUIC), h2 (MASQUE/TLS), wg (WireGuard)
# Blobs: quic_google, quic_vk, tls_google, tls_vk, stun_fake, zero64, fake_default_quic, fake_default_tls
# Zarp adds the WinDivert filter, lua-init and blobs itself.
#
# Examples:
# My QUIC | h3 | --payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=8
# My WG   | wg | --payload=wireguard_initiation --lua-desync=fake:blob=zero64:repeats=12
";
    }
}
