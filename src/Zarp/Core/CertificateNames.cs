using System;
using System.IO;
using System.Text;

namespace Zarp.Core
{
    /// <summary>Reads the organization attribute from a DER X.500 Name, never from display text.</summary>
    internal static class CertificateNames
    {
        public static string Organization(byte[] name)
        {
            if (name == null) return null;
            try
            {
                var document = new Der(name, 0, name.Length);
                var sequence = document.Read(0x30);
                document.End();
                string organization = null;
                while (sequence.Remaining > 0)
                {
                    var set = sequence.Read(0x31);
                    if (set.Remaining == 0) return null;
                    while (set.Remaining > 0)
                    {
                        var attribute = set.Read(0x30);
                        var oid = attribute.Read(0x06);
                        var value = attribute.ReadAny(out byte tag);
                        attribute.End();
                        if (oid.Remaining != 3 || oid.Data[oid.Position] != 0x55 ||
                            oid.Data[oid.Position + 1] != 0x04 || oid.Data[oid.Position + 2] != 0x0a) continue;
                        if (organization != null) return null; // ambiguous repeated O, including multi-valued RDNs
                        Encoding encoding;
                        switch (tag)
                        {
                            case 0x0c: encoding = new UTF8Encoding(false, true); break;
                            case 0x13: // PrintableString
                            case 0x14: encoding = Encoding.GetEncoding(28591); break; // TeletexString (ASCII issuer names)
                            case 0x1e: encoding = new UnicodeEncoding(true, false, true); break;
                            case 0x1c: encoding = new UTF32Encoding(true, false, true); break;
                            default: return null;
                        }
                        organization = encoding.GetString(value.Data, value.Position, value.Remaining);
                    }
                }
                return organization;
            }
            catch (Exception e) when (e is InvalidDataException || e is ArgumentException || e is OverflowException)
            {
                return null;
            }
        }

        // Only the definite-length TLV subset needed for an X.500 Name. Every nested value is bounded.
        sealed class Der
        {
            public readonly byte[] Data;
            public int Position;
            readonly int _end;
            public int Remaining => _end - Position;
            public Der(byte[] data, int start, int end) { Data = data; Position = start; _end = end; }
            public void End() { if (Remaining != 0) throw new InvalidDataException(); }
            public Der Read(byte expected)
            {
                var value = ReadAny(out byte tag);
                if (tag != expected) throw new InvalidDataException();
                return value;
            }
            public Der ReadAny(out byte tag)
            {
                if (Remaining < 2) throw new InvalidDataException();
                tag = Data[Position++];
                if ((tag & 0x1f) == 0x1f) throw new InvalidDataException();
                int length = Data[Position++];
                if ((length & 0x80) != 0)
                {
                    int count = length & 0x7f;
                    if (count == 0 || count > 4 || count > Remaining || Data[Position] == 0) throw new InvalidDataException();
                    length = 0;
                    for (int i = 0; i < count; i++) length = checked(length * 256 + Data[Position++]);
                    if (length < 128) throw new InvalidDataException();
                }
                if (length > Remaining) throw new InvalidDataException();
                var result = new Der(Data, Position, Position + length);
                Position += length;
                return result;
            }
        }
    }
}
