using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Zarp.Core
{
    /// <summary>
    /// Локализация. Строки лежат в Lang/&lt;код&gt;.txt (вшиты в exe) в формате «ключ = значение».
    /// Язык берётся из настроек; если там пусто - язык Windows, а если он не поддерживается - английский.
    /// Непереведённый ключ показывается по-английски, чтобы интерфейс никогда не оставался без текста.
    /// </summary>
    public static class L
    {
        public sealed class Language
        {
            public string Code { get; }
            /// <summary>Самоназвание: по нему язык найдёт человек, который не понимает текущий.</summary>
            public string NativeName { get; }

            public Language(string code, string nativeName)
            {
                Code = code;
                NativeName = nativeName;
            }
        }

        /// <summary>Поддерживаемые языки в порядке показа в меню.</summary>
        public static readonly IReadOnlyList<Language> Languages = new[]
        {
            new Language("en", "English"),
            new Language("ru", "Русский"),
            new Language("es", "Español"),
            new Language("pt", "Português"),
            new Language("zh", "中文"),
            new Language("hi", "हिन्दी"),
            new Language("fr", "Français"),
            new Language("de", "Deutsch"),
        };

        const string Fallback = "en";
        static readonly Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>();
        static Dictionary<string, string> _current = Table(Fallback);

        /// <summary>Код текущего языка.</summary>
        public static string Current { get; private set; } = Fallback;

        /// <summary>Язык сменился. Вызывается в потоке, который вызвал Apply (в приложении - поток окна).</summary>
        public static event Action Changed;

        public static bool IsSupported(string code) => code != null && Languages.Any(l => l.Code == code);

        /// <summary>Язык Windows, если он поддерживается, иначе английский.</summary>
        public static string SystemLanguage => Map(CultureInfo.CurrentUICulture);

        /// <summary>pt-BR → pt, zh-CN → zh, ja-JP → en.</summary>
        public static string Map(CultureInfo culture)
        {
            string code = culture?.TwoLetterISOLanguageName;
            return IsSupported(code) ? code : Fallback;
        }

        /// <summary>Значение из настроек → код языка. Пустое или неизвестное значение означает «как в системе».</summary>
        public static string Resolve(string setting) => IsSupported(setting) ? setting : SystemLanguage;

        /// <summary>Включить язык из настроек (null - как в системе).</summary>
        public static void Apply(string setting)
        {
            string code = Resolve(setting);
            if (code == Current) return;
            Current = code;
            _current = Table(code);
            Changed?.Invoke();
        }

        public static string T(string key) => Lookup(_current, key) ?? Lookup(Table(Fallback), key) ?? key;

        public static string T(string key, params object[] args)
        {
            string format = T(key);
            if (args == null || args.Length == 0) return format;
            try { return string.Format(CultureInfo.InvariantCulture, format, args); }
            catch (FormatException) { return format + " " + string.Join(", ", args); }
        }

        /// <summary>Все ключи языка (для проверок в тестах).</summary>
        public static IEnumerable<string> KeysOf(string code) => Table(code).Keys;

        /// <summary>Строка языка без подстановки английского (для проверок в тестах).</summary>
        public static string Raw(string code, string key) => Lookup(Table(code), key);

        static string Lookup(Dictionary<string, string> table, string key) =>
            table.TryGetValue(key, out var value) ? value : null;

        static Dictionary<string, string> Table(string code)
        {
            lock (Tables)
            {
                if (!Tables.TryGetValue(code, out var table))
                    Tables[code] = table = Parse(code);
                return table;
            }
        }

        static Dictionary<string, string> Parse(string code)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var stream = typeof(L).Assembly.GetManifestResourceStream("lang/" + code + ".txt"))
            {
                if (stream == null) return table;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        table[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                    }
                }
            }
            return table;
        }
    }

    /// <summary>
    /// Сообщение, которое переводится в момент показа: ключ и аргументы.
    /// Так статус и результаты проверок меняют язык вместе с интерфейсом.
    /// </summary>
    public sealed class Msg
    {
        public string Key { get; }
        public object[] Args { get; }

        public Msg(string key, params object[] args)
        {
            Key = key;
            Args = args ?? new object[0];
        }

        /// <summary>Аргументы в виде строк - для сохранения в настройках.</summary>
        public string[] ArgStrings => Args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)).ToArray();

        public override string ToString() => L.T(Key, Args);
    }
}
