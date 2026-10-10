using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Zarp.Core
{
    /// <summary>IP-адреса и сети в строгом виде (как netip в Go): без сокращённых записей, нулей слева и зон.</summary>
    public static class IpNet
    {
        /// <summary>Разобрать адрес или сеть. bare: разрешён адрес без длины (тогда /32 или /128).</summary>
        public static bool TryParse(string text, bool bare, out byte[] ip, out int prefix)
        {
            ip = null;
            prefix = 0;
            string address = text;
            string length = null;
            int slash = text.IndexOf('/');
            if (slash >= 0)
            {
                address = text.Substring(0, slash);
                length = text.Substring(slash + 1);
            }
            else if (!bare) return false;
            if (!TryAddress(address, out ip)) return false;
            int bits = ip.Length * 8;
            if (length == null) { prefix = bits; return true; }
            if (length.Length == 0 || length.Length > 3 || !length.All(c => c >= '0' && c <= '9') || (length.Length > 1 && length[0] == '0')) return false;
            prefix = int.Parse(length);
            return prefix <= bits;
        }

        static bool TryAddress(string text, out byte[] bytes)
        {
            bytes = null;
            if (text.Length == 0 || text.IndexOf('%') >= 0) return false;
            if (text.IndexOf(':') < 0)
            {
                var parts = text.Split('.');
                if (parts.Length != 4) return false;
                var v4 = new byte[4];
                for (int i = 0; i < 4; i++)
                {
                    string p = parts[i];
                    if (p.Length == 0 || p.Length > 3 || !p.All(c => c >= '0' && c <= '9') || (p.Length > 1 && p[0] == '0')) return false;
                    int value = int.Parse(p);
                    if (value > 255) return false;
                    v4[i] = (byte)value;
                }
                bytes = v4;
                return true;
            }
            if (!text.All(c => Uri.IsHexDigit(c) || c == ':' || c == '.')) return false;
            if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
            bytes = ip.GetAddressBytes();
            return true;
        }

        /// <summary>Сеть с обнулёнными битами хоста: «10.1.2.3/8» превращается в «10.0.0.0/8».</summary>
        public static string Format(byte[] ip, int prefix)
        {
            var masked = (byte[])ip.Clone();
            for (int i = 0; i < masked.Length; i++)
            {
                int keep = Math.Max(0, Math.Min(8, prefix - i * 8));
                masked[i] &= (byte)(0xFF << (8 - keep));
            }
            return new IPAddress(masked) + "/" + prefix;
        }
    }

    /// <summary>Категория базы: записи (CIDR или домены) в исходном виде и признак обратного совпадения (только GeoIP).</summary>
    public sealed class GeoEntry
    {
        public readonly List<ArraySegment<byte>> Records = new List<ArraySegment<byte>>();
        public bool Inverse;
    }

    public sealed class GeoDatabase
    {
        public readonly Dictionary<string, GeoEntry> Entries = new Dictionary<string, GeoEntry>(StringComparer.Ordinal);
    }

    /// <summary>Домен из GeoSite: тип (0 - часть имени, 1 - регулярное выражение, 2 - домен с поддоменами, 3 - точное имя) и атрибуты.</summary>
    public sealed class GeoDomain
    {
        public int Type;
        public string Value;
        public readonly Dictionary<string, bool> Attributes = new Dictionary<string, bool>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Файлы V2Ray/Xray .dat (GeoIPList и GeoSiteList): protobuf, разбирается вручную.
    /// https://github.com/v2fly/v2ray-core/blob/master/app/router/routercommon/common.proto
    /// Всё проверяется строго: повреждённый или обрезанный файл - ошибка, а не пустая база.
    /// </summary>
    public static class GeoDat
    {
        public const int MaxBytes = 64 << 20;
        const int MaxCategories = 20000, MaxRecords = 2000000;
        static readonly Regex TagPattern = new Regex(@"^[a-zA-Z0-9_!.\-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool ValidTag(string tag) => tag != null && TagPattern.IsMatch(tag);

        struct Field
        {
            public int Number, WireType;
            public ArraySegment<byte> Bytes;
            public ulong Value;
        }

        static InvalidDataException Bad(string message) => new InvalidDataException(message);

        static ulong Varint(byte[] buffer, ref int pos, int end)
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (pos >= end) throw Bad("truncated protobuf field");
                byte b = buffer[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw Bad("invalid protobuf field");
        }

        static IEnumerable<Field> Walk(ArraySegment<byte> data)
        {
            byte[] buffer = data.Array;
            int pos = data.Offset, end = data.Offset + data.Count;
            while (pos < end)
            {
                ulong tag = Varint(buffer, ref pos, end);
                int number = (int)(tag >> 3), wire = (int)(tag & 7);
                if (number <= 0 || tag >> 3 > int.MaxValue) throw Bad("invalid protobuf field");
                var field = new Field { Number = number, WireType = wire };
                switch (wire)
                {
                    case 0:
                        field.Value = Varint(buffer, ref pos, end);
                        break;
                    case 1:
                        if (end - pos < 8) throw Bad("truncated protobuf field");
                        pos += 8;
                        break;
                    case 2:
                        ulong length = Varint(buffer, ref pos, end);
                        if (length > (ulong)(end - pos)) throw Bad("truncated protobuf field");
                        field.Bytes = new ArraySegment<byte>(buffer, pos, (int)length);
                        pos += (int)length;
                        break;
                    case 5:
                        if (end - pos < 4) throw Bad("truncated protobuf field");
                        pos += 4;
                        break;
                    default:
                        throw Bad("unsupported protobuf wire type");
                }
                yield return field;
            }
        }

        static string Text(ArraySegment<byte> bytes) => Encoding.UTF8.GetString(bytes.Array, bytes.Offset, bytes.Count);

        /// <summary>Прочитать базу целиком. kind: «geoip» или «geosite».</summary>
        public static GeoDatabase Read(string path, string kind)
        {
            if (kind != "geoip" && kind != "geosite") throw new ArgumentException("unknown database type");
            byte[] data;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (file.Length == 0 || file.Length > MaxBytes) throw Bad("invalid database size");
                    data = new byte[file.Length];
                    int done = 0, n;
                    while (done < data.Length && (n = file.Read(data, done, data.Length - done)) > 0) done += n;
                    if (done != data.Length) throw Bad("invalid database size");
                }
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException || e is ArgumentException)
            {
                throw new FileNotFoundException(kind + ": database is not downloaded");
            }

            var db = new GeoDatabase();
            int records = 0;
            foreach (var field in Walk(new ArraySegment<byte>(data)))
            {
                if (field.Number != 1) continue;
                if (field.WireType != 2) throw Bad("invalid database entry");
                string name = null;
                var entry = new GeoEntry();
                foreach (var f in Walk(field.Bytes))
                {
                    switch (f.Number)
                    {
                        case 1:
                            if (f.WireType != 2) throw Bad("invalid category");
                            name = Text(f.Bytes).ToLowerInvariant();
                            break;
                        case 2:
                            if (f.WireType != 2 || records >= MaxRecords) throw Bad("invalid record");
                            records++;
                            entry.Records.Add(f.Bytes);
                            break;
                        case 3:
                            if (kind == "geoip")
                            {
                                if (f.WireType != 0) throw Bad("invalid inverse flag");
                                entry.Inverse = f.Value != 0;
                            }
                            break;
                    }
                }
                if (!ValidTag(name) || entry.Records.Count == 0 || db.Entries.Count >= MaxCategories) throw Bad("invalid or empty category");
                if (db.Entries.ContainsKey(name)) throw Bad("duplicate category");
                db.Entries[name] = entry;
            }
            if (db.Entries.Count == 0) throw Bad("no categories found");
            return db;
        }

        static bool ReadCidr(ArraySegment<byte> record, out byte[] ip, out int prefix)
        {
            ip = null;
            ulong length = 0;
            foreach (var f in Walk(record))
            {
                if (f.Number == 1)
                {
                    if (f.WireType != 2) throw Bad("invalid IP");
                    ip = f.Bytes.ToArray();
                }
                if (f.Number == 2)
                {
                    if (f.WireType != 0) throw Bad("invalid prefix");
                    length = f.Value;
                }
            }
            prefix = 0;
            if (ip == null || (ip.Length != 4 && ip.Length != 16) || length > (ulong)(ip.Length * 8)) return false;
            prefix = (int)length;
            return true;
        }

        /// <summary>Запись GeoIP как «адрес/длина» с обнулёнными битами хоста.</summary>
        public static string Cidr(ArraySegment<byte> record)
        {
            if (!ReadCidr(record, out var ip, out int prefix)) throw Bad("invalid CIDR");
            return IpNet.Format(ip, prefix);
        }

        public static GeoDomain ParseDomain(ArraySegment<byte> record)
        {
            var domain = new GeoDomain();
            foreach (var f in Walk(record))
            {
                switch (f.Number)
                {
                    case 1:
                        if (f.WireType != 0) throw Bad("invalid domain type");
                        domain.Type = f.Value > 100 ? 100 : (int)f.Value;
                        break;
                    case 2:
                        if (f.WireType != 2) throw Bad("invalid domain");
                        try { domain.Value = new UTF8Encoding(false, true).GetString(f.Bytes.Array, f.Bytes.Offset, f.Bytes.Count); }
                        catch (ArgumentException) { throw Bad("invalid domain record"); }
                        break;
                    case 3:
                        if (f.WireType != 2) throw Bad("invalid attribute");
                        string key = null;
                        bool present = true;
                        foreach (var a in Walk(f.Bytes))
                        {
                            if (a.Number == 1)
                            {
                                if (a.WireType != 2) throw Bad("invalid attribute key");
                                key = Text(a.Bytes).ToLowerInvariant();
                            }
                            if (a.Number == 2)
                            {
                                if (a.WireType != 0) throw Bad("invalid attribute value");
                                present = a.Value != 0;
                            }
                        }
                        if (!ValidTag(key)) throw Bad("invalid attribute name");
                        domain.Attributes[key] = present;
                        break;
                }
            }
            if (domain.Type > 3 || string.IsNullOrEmpty(domain.Value) || domain.Value.Length > 4096 ||
                domain.Value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw Bad("invalid domain record");
            // Регулярные выражения здесь не проверяются: движок использует RE2, а .NET понимает другой синтаксис.
            // Используемые правила проверяет сам sing-box перед подключением (sing-box check).
            return domain;
        }

        /// <summary>
        /// Проверить весь файл до замены рабочей базы. Возвращает имена категорий по алфавиту.
        /// </summary>
        public static List<string> Info(string kind, string path)
        {
            var db = Read(path, kind);
            foreach (var entry in db.Entries.Values)
                foreach (var record in entry.Records)
                {
                    if (kind == "geoip")
                    {
                        if (!ReadCidr(record, out _, out _)) throw Bad("invalid CIDR");
                    }
                    else ParseDomain(record);
                }
            return db.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
    }
}
