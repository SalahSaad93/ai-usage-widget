using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AiUsageWidget.Core
{
    // Plain UTF-8 text log, one line per entry ("2026-11-14T15:04:05Z LEVEL message"),
    // rotated into log.old.txt at 256 KB. Callers pass plain messages and exceptions
    // only: no token, key, Authorization header or credentials-file content ever reaches
    // this class. Nothing here ever throws — a logging failure is silently dropped.
    public static class Log
    {
        const long RotateBytes = 256 * 1024;

        static readonly object gate = new object();

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        static void Write(string level, string message, Exception ex)
        {
            try
            {
                if (message == null) message = "";
                var line = new StringBuilder();
                line.Append(DateTime.UtcNow.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
                    CultureInfo.InvariantCulture));
                line.Append(' ').Append(level).Append(' ').Append(OneLine(message));
                if (ex != null) line.Append(' ').Append(OneLine(ex.ToString()));

                lock (gate)
                {
                    string path = AppPaths.LogPath;
                    RotateIfNeeded(path);
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.AppendAllText(path, line.ToString() + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never throw; a failed log write is dropped.
            }
        }

        // The oversized log.txt becomes log.old.txt, replacing any previous one.
        static void RotateIfNeeded(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= RotateBytes) return;
            string old = Path.Combine(Path.GetDirectoryName(path), "log.old.txt");
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
        }

        // One entry is one line: embedded newlines are flattened so the file stays
        // line-oriented (exception stack traces included).
        static string OneLine(string text)
        {
            if (text == null) return "";
            return text.Replace("\r", "").Replace("\n", " | ");
        }
    }
}
