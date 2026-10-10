using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    /// Если задан Profile, это не стратегия обхода, а собственный сервер, который заменяет WARP.
    /// </summary>
    public sealed class Strategy
    {
        public string Id;
        public string Name { get; set; }
        public WarpTransport Transport;
        public string Args;
        public bool Custom;
        public string LegacyId;
        /// <summary>
        /// Страны (ru, ir, cn), для которых стратегию стоит проверять. Это метка кандидата, а не подтверждение,
        /// что она работает у любого провайдера страны. Пустой список: стране не назначена, видна только при «All».
        /// </summary>
        public string[] Countries = new string[0];
        /// <summary>Собственный сервер (VLESS, Trojan, Hysteria2) вместо WARP.</summary>
        public ProxyProfile Profile;

        public bool IsProxy => Profile != null;
        public bool UsesZapret => !IsProxy && !string.IsNullOrWhiteSpace(Args);
        public string ProtocolTitle => IsProxy ? Profile.Title : TransportTitle(Transport);

        public Strategy Clone() => new Strategy
        {
            Id = Id, Name = Name, Transport = Transport, Args = Args, Custom = Custom, LegacyId = LegacyId,
            Countries = Countries, Profile = Profile,
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
        // Стратегии именно под рукопожатие WARP. Клиент WARP по умолчанию работает по MASQUE, поэтому здесь два механизма:
        //  * MASQUE/HTTP3: QUIC Initial с SNI consumer-masque.cloudflareclient.com (post-quantum, 2 пакета). Помогают
        //    фейки перед ним и IP-фрагментация;
        //  * MASQUE/HTTP2: TLS ClientHello по TCP 443 (`warp-cli tunnel masque-options set h2-only`). Помогают
        //    сегментация по точкам SNI, seqovl и фейки с испорченным md5/seq/ack.
        // Фейк должен выглядеть как разрешённый трафик: реальные пакеты google/vk/gosuslugi, а не нули. «Пустые» фейки
        // DPI отбрасывает (проверено: fake_default_quic не проходит), поэтому их здесь нет.
        // Синтаксис взят из blockcheck2 и документации zapret2 (релиз, который вшит в Zarp), а не придуман: например, seqovl у
        // multidisorder должен быть меньше первой позиции разреза, иначе zapret2 его молча отменяет.
        // Протокол WireGuard из встроенного набора убран: MASQUE стал протоколом WARP по умолчанию, а рукопожатие WireGuard
        // (148 байт с фиксированной сигнатурой) блокируется проще всего. Свою стратегию под него можно дописать
        // в strategies.txt (транспорт wg).
        // Стратегии без zapret («WARP как есть») намеренно нет: Zarp нужен там, где WARP сам не подключается.
        // Порядок важен: поиск идёт сверху вниз и в быстром режиме останавливается на нескольких рабочих.
        // Поэтому первыми стоят самые проверенные варианты, причём h3 и h2 чередуются, а новые, ещё не обкатанные
        // варианты (разбиение по host/sld/endhost, fake google x12) стоят в конце.
        // Имена технические и одинаковые на всех языках: fake, ttl - термины zapret из аргументов.

        static readonly string[] AllCountries = { "ru", "ir", "cn" };
        static readonly string[] RussiaOnly = { "ru" };
        // Фейки российских сервисов имеют смысл там, где DPI пропускает именно их: метка только «ru».
        static readonly string[] RussianFakes = { "vk", "gosuslugi" };
        // (эти поля стоят выше списка: статические инициализаторы выполняются по порядку, а S() ими пользуется)
        static readonly Strategy[] BuiltIn =
        {
            S("warp-q-google6",   "WARP QUIC: fake google ×6",            WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=6"),
            S("warp-q-google3",   "WARP QUIC: fake google ×3",            WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=3"),
            S("warp-q-vk6",       "WARP QUIC: fake vk ×6",                WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_vk:repeats=6"),
            S("warp-t-multidisorder", "WARP TLS: disorder on SNI points", WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=multidisorder:pos=1,sniext+1,host+1,midsld-2,midsld,midsld+2,endhost-1"),
            S("warp-q-google11",  "WARP QUIC: fake google ×11",           WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=11"),
            S("warp-q-google-vk", "WARP QUIC: fakes google + vk",         WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=3 --lua-desync=fake:blob=quic_vk:repeats=3"),
            S("warp-t-google-md5","WARP TLS: fake google md5 + split",    WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=fake:blob=tls_google:tcp_md5:repeats=6 --lua-desync=multisplit:pos=1,midsld"),
            S("warp-t-seqovl",    "WARP TLS: seqovl google",              WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google"),
            S("warp-q-google-ttl","WARP QUIC: fake google ttl=4 ×6",      WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:ip_ttl=4:ip6_ttl=4:repeats=6"),
            S("warp-q-ipfrag8",   "WARP QUIC: IP fragmentation",          WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=send:ipfrag:ipfrag_pos_udp=8 --lua-desync=drop"),
            S("warp-t-disorder-seqovl", "WARP TLS: disorder + seqovl",    WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=multidisorder:pos=2:seqovl=1:seqovl_pattern=tls_google"),
            S("warp-t-vk-seq",    "WARP TLS: fake vk badseq + disorder",  WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=fake:blob=tls_vk:tcp_seq=-3000:repeats=6 --lua-desync=multidisorder:pos=1,midsld"),
            S("warp-q-fake-ipfrag", "WARP QUIC: fake + IP fragmentation", WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=6 --lua-desync=send:ipfrag:ipfrag_pos_udp=16 --lua-desync=drop"),
            S("warp-t-hostfake",  "WARP TLS: hostfakesplit vk.com",       WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=hostfakesplit:host=vk.com:tcp_md5"),
            S("warp-t-fake-multi","WARP TLS: fake gosuslugi badack + split", WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=fake:blob=tls_gosuslugi:tcp_ack=-66000:tcp_ts_up:repeats=6 --lua-desync=multisplit:pos=1,midsld"),
            // Разбиение ClientHello по маркерам zapret2 host, sld и endhost и ещё один вариант с числом фейков.
            S("warp-t-split-host", "WARP TLS: split 1,host",              WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=multisplit:pos=1,host"),
            S("warp-q-google12",  "WARP QUIC: fake google ×12",           WarpTransport.MasqueH3,
              "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=12"),
            S("warp-t-split-sld", "WARP TLS: split 1,sld",                WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=multisplit:pos=1,sld"),
            S("warp-t-split-endhost", "WARP TLS: split 1,endhost",        WarpTransport.MasqueH2,
              "--payload=tls_client_hello --lua-desync=multisplit:pos=1,endhost"),
        };

        /// <summary>Встроенные стратегии (для тестов и описания). Список только для чтения.</summary>
        public static IReadOnlyList<Strategy> BuiltInStrategies => BuiltIn;

        static Strategy S(string id, string name, WarpTransport t, string args) => new Strategy
        {
            Id = id, Name = name, Transport = t, Args = args,
            Countries = RussianFakes.Any(f => args.Contains(f)) ? RussiaOnly : AllCountries,
        };

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
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                // четвёртое поле (страны) необязательно: строки старого формата из трёх полей остаются верными
                var parts = line.Split(new[] { '|' }, 4);
                if (parts.Length < 3 || !Strategy.TryParseTransport(parts[1], out var t))
                {
                    Log.Write(L.T("log.customSkipped", CustomFileName, line));
                    continue;
                }
                string name = parts[0].Trim();
                string args = parts[2].Trim();
                if (name.Length == 0) continue;
                string id;
                // страны в идентификатор не входят: добавленная метка не должна обнулять сохранённые результаты
                using (var hash = SHA256.Create())
                    id = "custom-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(name + "\n" + t + "\n" + args))).Replace("-", "").ToLowerInvariant();
                if (!seen.Add(id)) continue;
                yield return new Strategy
                {
                    Id = id,
                    LegacyId = "custom-" + name.ToLowerInvariant().Replace(' ', '-'),
                    Name = "★ " + name,
                    Transport = t,
                    Args = args,
                    Custom = true,
                    Countries = parts.Length > 3 ? StrategyFilter.ParseCountries(parts[3]) : new string[0],
                };
            }
        }

        // Файл настройки для опытных пользователей: синтаксис технический, поэтому комментарии на английском.
        const string CustomTemplate =
@"# Custom Zarp strategies. One line = one strategy:
#   Name | transport | winws2 profile arguments | countries (optional)
# transport: h3 (MASQUE/QUIC), h2 (MASQUE/TLS), wg (WireGuard)
# countries: ru,ir,cn. Lines without them stay available under ""All"" in the country filter.
# Blobs: quic_google, quic_vk, tls_google, tls_vk, tls_gosuslugi, stun_fake, zero64, fake_default_quic, fake_default_tls
# Zarp adds the WinDivert filter, lua-init and blobs itself.
#
# Examples:
# My QUIC | h3 | --payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=8
# My TLS  | h2 | --payload=tls_client_hello --lua-desync=multisplit:pos=1,sld | ru,ir,cn
# My WG   | wg | --payload=wireguard_initiation --lua-desync=fake:blob=zero64:repeats=12
";
    }
}
