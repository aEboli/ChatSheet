using System;
using System.Diagnostics;
using System.IO;

namespace ChatWord.AddIn.Hosts
{
    internal enum WordHostKind
    {
        Unknown = 0,
        MicrosoftWord = 1,
        WpsWriter = 2,
    }

    internal static class WordHostProbe
    {
        internal static WordHostKind Detect(object application, string registeredProgId = null)
        {
            var process = CurrentProcessName();
            if (process.Equals("wps", StringComparison.OrdinalIgnoreCase) ||
                process.Equals("kwps", StringComparison.OrdinalIgnoreCase) ||
                process.Equals("wpscloudsvr", StringComparison.OrdinalIgnoreCase))
            {
                return WordHostKind.WpsWriter;
            }
            if (process.Equals("winword", StringComparison.OrdinalIgnoreCase))
            {
                return WordHostKind.MicrosoftWord;
            }
            if (!string.IsNullOrWhiteSpace(registeredProgId) &&
                registeredProgId.IndexOf("kwps", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return WordHostKind.WpsWriter;
            }
            var name = application == null ? string.Empty : WordCom.String(application, "Name");
            if (name.IndexOf("WPS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Writer", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return WordHostKind.WpsWriter;
            }
            if (name.IndexOf("Word", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return WordHostKind.MicrosoftWord;
            }
            return WordHostKind.Unknown;
        }

        internal static string CurrentProcessName()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    return Path.GetFileNameWithoutExtension(process.MainModule?.FileName ?? process.ProcessName);
                }
            }
            catch { return string.Empty; }
        }

        internal static string DisplayName(WordHostKind kind)
        {
            switch (kind)
            {
                case WordHostKind.MicrosoftWord: return "Microsoft Word";
                case WordHostKind.WpsWriter: return "WPS Writer";
                default: return "未知 Word 宿主";
            }
        }

        internal static string Describe(object application, string progId)
        {
            var kind = Detect(application, progId);
            return string.Format(
                "{0}（进程={1}.exe ProgID={2} Name={3} Version={4} Build={5}）",
                DisplayName(kind), CurrentProcessName(), progId ?? string.Empty,
                WordCom.String(application, "Name", "<无 Name>"),
                WordCom.String(application, "Version"), WordCom.String(application, "Build"));
        }
    }
}
