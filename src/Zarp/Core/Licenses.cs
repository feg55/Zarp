using System;
using System.IO;
using System.Linq;

namespace Zarp.Core
{
    /// <summary>
    /// Тексты лицензий Zarp и вшитых компонентов (zapret2, WinDivert, Cygwin, LuaJIT, zlib).
    /// Лежат в exe как ресурсы "licenses/..." и распаковываются рядом с данными программы,
    /// чтобы сопровождать распространяемые бинарники, как того требуют MIT и LGPL.
    /// </summary>
    public static class Licenses
    {
        public const string NoticesFile = "THIRD_PARTY_NOTICES.md";

        /// <summary>Распаковать лицензии в dataDir\licenses (перезаписывая изменившиеся). Возвращает путь к папке.</summary>
        public static string Extract(string dataDir)
        {
            string dir = Path.Combine(dataDir, "licenses");
            var asm = typeof(Licenses).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("licenses/", StringComparison.Ordinal)))
            {
                try
                {
                    string dst = Path.Combine(dir, name.Substring("licenses/".Length));
                    using (var s = asm.GetManifestResourceStream(name))
                    {
                        if (File.Exists(dst) && new FileInfo(dst).Length == s.Length) continue;
                        Directory.CreateDirectory(dir);
                        using (var f = File.Create(dst)) s.CopyTo(f);
                    }
                }
                catch (Exception e)
                {
                    Log.Write(L.T("log.licenseFailed", name, e.Message));
                }
            }
            return dir;
        }
    }
}
