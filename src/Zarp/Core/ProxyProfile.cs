using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Zarp.Core
{
    public enum ProxyProtocol { Vless, Trojan, Hysteria2 }

    /// <summary>
    /// Собственный сервер из одной ссылки vless://, trojan:// или hysteria2:// (hy2://). Поддерживается ровно то, что
    /// описано в документации: неизвестные параметры и транспорты отвергаются, а не отбрасываются молча.
    /// Пароль и UUID не попадают ни в имя стратегии, ни в журнал, ни в текст ошибок.
    /// </summary>
    public sealed class ProxyProfile
    {
        public ProxyProtocol Protocol { get; private set; }
        public string Host { get; private set; }
        public int Port { get; private set; }
        /// <summary>Идентификатор стратегии: от ссылки без подписи, поэтому смена любого параметра подключения сбрасывает результаты.</summary>
        public string Id { get; private set; }

        string _credential, _security, _network, _flow;
        string _sni, _alpn, _fingerprint, _publicKey, _shortId, _path, _hostHeader, _serviceName, _obfsPassword;
        bool _insecure, _obfs;

        public string Title
        {
            get
            {
                switch (Protocol)
                {
                    case ProxyProtocol.Vless: return "VLESS";
                    case ProxyProtocol.Trojan: return "Trojan";
                    default: return "Hysteria2";
                }
            }
        }

        /// <summary>Адрес для показа: host:port или [IPv6]:port.</summary>
        public string Endpoint => (Host.Contains(":") ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);

        /// <summary>Стратегия-заглушка для списка: у сервера нет обхода DPI, он просто проверяется и подключается.</summary>
        public Strategy ToStrategy() => new Strategy
        {
            Id = Id, Name = Title + ": " + Endpoint, Transport = WarpTransport.MasqueH3, Args = "", Custom = true, Profile = this,
        };

        public static bool TryParse(string text, out ProxyProfile profile)
        {
            try { profile = Parse(text); return true; }
            catch (FormatException) { profile = null; return false; }
        }

        /// <summary>Разобрать ссылку. При любой ошибке - FormatException("proxy.invalid") без пароля в тексте.</summary>
        public static ProxyProfile Parse(string text)
        {
            try { return ParseCore((text ?? "").Trim()); }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // сообщения разборщиков могут содержать фрагменты ссылки, а в ней пароль
                throw new FormatException("proxy.invalid");
            }
        }

        static readonly string[] KnownParameters =
        {
            "security", "sni", "peer", "insecure", "allowInsecure", "alpn", "fp", "pbk", "sid", "flow", "encryption",
            "type", "path", "host", "serviceName", "obfs", "obfs-password",
        };

        static readonly Regex Uuid = new Regex("^[a-fA-F0-9]{8}(-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}$", RegexOptions.Compiled);
        static readonly Regex RealityKey = new Regex("^[A-Za-z0-9_-]{43}$", RegexOptions.Compiled);
        static readonly Regex HostName = new Regex("^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$", RegexOptions.Compiled);

        static void Require(bool condition)
        {
            if (!condition) throw new FormatException();
        }

        static ProxyProfile ParseCore(string text)
        {
            Require(text.Length >= 1 && text.Length <= 8192);
            int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            Require(schemeEnd > 0);
            ProxyProtocol protocol;
            switch (text.Substring(0, schemeEnd).ToLowerInvariant())
            {
                case "vless": protocol = ProxyProtocol.Vless; break;
                case "trojan": protocol = ProxyProtocol.Trojan; break;
                case "hy2": case "hysteria2": protocol = ProxyProtocol.Hysteria2; break;
                default: throw new FormatException();
            }

            string rest = text.Substring(schemeEnd + 3);
            int hash = rest.IndexOf('#'); // имя сервера после # - только подпись
            if (hash >= 0) rest = rest.Substring(0, hash);
            string query = "";
            int question = rest.IndexOf('?');
            if (question >= 0) { query = rest.Substring(question + 1); rest = rest.Substring(0, question); }
            string path = "";
            int slash = rest.IndexOf('/');
            if (slash >= 0) { path = rest.Substring(slash); rest = rest.Substring(0, slash); }
            Require(path.Length == 0 || path == "/");

            int at = rest.LastIndexOf('@');
            Require(at > 0);
            string credential = Decode(rest.Substring(0, at));
            string authority = rest.Substring(at + 1);
            Require(credential.Length > 0 && !credential.Any(char.IsControl));

            string host, portText = "";
            if (authority.StartsWith("["))
            {
                int close = authority.IndexOf(']');
                Require(close > 1);
                host = authority.Substring(1, close - 1);
                string after = authority.Substring(close + 1);
                Require(after.Length == 0 || after[0] == ':');
                if (after.Length > 0) portText = after.Substring(1);
                Require(IPAddress.TryParse(host, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6);
            }
            else
            {
                int colon = authority.IndexOf(':');
                host = colon < 0 ? authority : authority.Substring(0, colon);
                if (colon >= 0) portText = authority.Substring(colon + 1);
                Require(host.Length > 0 && host.Length <= 253 && HostName.IsMatch(host));
            }
            Require(host.IndexOf('%') < 0);
            int port;
            if (portText.Length == 0) { Require(protocol == ProxyProtocol.Hysteria2); port = 443; }
            else
            {
                Require(portText.Length <= 5 && portText.All(c => c >= '0' && c <= '9'));
                port = int.Parse(portText, CultureInfo.InvariantCulture);
            }
            Require(port >= 1 && port <= 65535);

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var piece in query.Split('&'))
            {
                if (piece.Length == 0) continue;
                int eq = piece.IndexOf('=');
                string key = Decode(eq < 0 ? piece : piece.Substring(0, eq));
                string value = eq < 0 ? "" : Decode(piece.Substring(eq + 1));
                Require(!parameters.ContainsKey(key));
                parameters[key] = value;
            }
            Require(parameters.Keys.All(k => KnownParameters.Contains(k)));
            string Get(string key) => parameters.TryGetValue(key, out var v) ? v : null;

            Require(Get("encryption") == null || Get("encryption") == "" || Get("encryption") == "none");
            string security = Get("security") ?? (protocol == ProxyProtocol.Vless ? "none" : "tls");
            Require(security == "none" || security == "tls" || security == "reality");
            Require(protocol == ProxyProtocol.Vless || security == "tls");
            string network = Get("type") ?? "tcp";
            Require(network == "tcp" || network == "ws" || network == "grpc" || network == "httpupgrade");
            Require(protocol != ProxyProtocol.Hysteria2 || network == "tcp");
            string flow = Get("flow") ?? "";
            Require(flow == "" || flow == "xtls-rprx-vision");
            Require(flow.Length == 0 || (protocol == ProxyProtocol.Vless && network == "tcp" && security != "none"));
            if (protocol == ProxyProtocol.Vless) Require(Uuid.IsMatch(credential));
            foreach (var key in new[] { "insecure", "allowInsecure" })
            {
                string flag = Get(key);
                Require(flag == null || flag == "0" || flag == "1" || flag == "true" || flag == "false");
            }

            var profile = new ProxyProfile
            {
                Protocol = protocol, Host = host, Port = port,
                _credential = credential, _security = security, _network = network, _flow = flow,
                _sni = Get("sni") ?? Get("peer") ?? host,
                _insecure = new[] { Get("insecure"), Get("allowInsecure") }.Any(v => v == "1" || v == "true"),
                _alpn = string.IsNullOrWhiteSpace(Get("alpn")) ? null : Get("alpn"),
                _fingerprint = Get("fp"),
                _path = Get("path") ?? "/", _hostHeader = Get("host"), _serviceName = Get("serviceName") ?? "",
            };
            if (security == "reality")
            {
                string key = Get("pbk");
                Require(key != null && RealityKey.IsMatch(key));
                string shortId = Get("sid") ?? "";
                Require(shortId.Length <= 16 && shortId.Length % 2 == 0 && shortId.All(Uri.IsHexDigit));
                profile._publicKey = key;
                profile._shortId = shortId;
            }
            string obfs = Get("obfs");
            if (obfs != null)
            {
                Require(protocol == ProxyProtocol.Hysteria2 && obfs == "salamander");
                string password = Get("obfs-password");
                Require(!string.IsNullOrEmpty(password));
                profile._obfs = true;
                profile._obfsPassword = password;
            }
            // подпись после # (имя сервера) на подключение не влияет, поэтому в идентификатор не входит
            int label = text.IndexOf('#');
            using (var sha = SHA256.Create())
                profile.Id = "proxy-" + string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(label >= 0 ? text.Substring(0, label) : text)).Take(12).Select(b => b.ToString("x2")));
            return profile;
        }

        /// <summary>Как URLDecoder, но «+» остаётся плюсом: так пароли с + не портятся. Битый %-код - ошибка.</summary>
        static string Decode(string value)
        {
            for (int i = 0; i < value.Length; i++)
                if (value[i] == '%')
                    Require(i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]));
            return Uri.UnescapeDataString(value);
        }

        /// <summary>
        /// Исходящее подключение sing-box с тегом «proxy». address - адрес, на который реально идёт соединение
        /// (обычно уже разрешённый IP сервера); имя в TLS (SNI) остаётся тем, что указано в ссылке.
        /// </summary>
        public Dictionary<string, object> BuildOutbound(string address = null)
        {
            var o = new Dictionary<string, object>
            {
                ["type"] = Protocol == ProxyProtocol.Vless ? "vless" : Protocol == ProxyProtocol.Trojan ? "trojan" : "hysteria2",
                ["tag"] = "proxy",
                ["server"] = address ?? Host,
                ["server_port"] = Port,
                ["domain_resolver"] = "bootstrap",
                ["connect_timeout"] = "15s",
            };
            if (Protocol == ProxyProtocol.Vless)
            {
                o["uuid"] = _credential;
                o["flow"] = _flow;
                o["packet_encoding"] = "xudp";
            }
            else o["password"] = _credential;

            if (_security != "none")
            {
                var tls = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["server_name"] = _sni,
                    ["insecure"] = _insecure,
                };
                if (_alpn != null) tls["alpn"] = _alpn.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
                if (Protocol != ProxyProtocol.Hysteria2 && (_fingerprint != null || _security == "reality"))
                    tls["utls"] = new Dictionary<string, object> { ["enabled"] = true, ["fingerprint"] = _fingerprint ?? "chrome" };
                if (_security == "reality")
                    tls["reality"] = new Dictionary<string, object> { ["enabled"] = true, ["public_key"] = _publicKey, ["short_id"] = _shortId };
                o["tls"] = tls;
            }
            if (_network != "tcp")
            {
                var transport = new Dictionary<string, object> { ["type"] = _network };
                if (_network == "grpc") transport["service_name"] = _serviceName;
                else
                {
                    transport["path"] = _path;
                    if (_hostHeader != null)
                    {
                        if (_network == "ws") transport["headers"] = new Dictionary<string, object> { ["Host"] = _hostHeader };
                        else transport["host"] = _hostHeader;
                    }
                }
                o["transport"] = transport;
            }
            if (_obfs)
                o["obfs"] = new Dictionary<string, object> { ["type"] = "salamander", ["password"] = _obfsPassword };
            return o;
        }
    }
}
