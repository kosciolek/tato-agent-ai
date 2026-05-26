using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AgentReadonly.Services
{
    public static class AppLog
    {
        private const int MaxValueLength = 12000;
        private const uint AttachParentProcess = 0xFFFFFFFF;
        private static readonly object sync = new object();
        private static bool initialized;

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Debug(string message)
        {
            Write("DEBUG", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        public static string Truncate(string value)
        {
            if (value == null)
                return "";
            value = value.Replace("\r", "\\r").Replace("\n", "\\n");
            if (value.Length <= MaxValueLength)
                return value;
            return value.Substring(0, MaxValueLength) + "... <truncated " + (value.Length - MaxValueLength) + " chars>";
        }

        private static void Write(string level, string message, Exception exception)
        {
            EnsureInitialized();

            string line = DateTime.Now.ToString("o") + " [" + level + "] " + message;
            if (exception != null)
                line += Environment.NewLine + exception;

            lock (sync)
            {
                WriteStdout(level, line);
                WriteFile(AppPaths.LogPath, line);
                WriteFile(AppPaths.DailyLogPath, line);
            }
        }

        private static void EnsureInitialized()
        {
            if (initialized)
                return;

            lock (sync)
            {
                if (initialized)
                    return;

                try
                {
                    AttachConsole(AttachParentProcess);
                }
                catch
                {
                }

                initialized = true;
            }
        }

        private static void WriteStdout(string level, string text)
        {
            try
            {
                if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
                    Console.Error.WriteLine(text);
                else
                    Console.Out.WriteLine(text);
            }
            catch
            {
            }

            try
            {
                Trace.WriteLine(text);
            }
            catch
            {
            }
        }

        private static void WriteFile(string path, string text)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        writer.WriteLine(text);
                    }
                    return;
                }
                catch
                {
                    Thread.Sleep(20);
                }
            }
        }

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(uint dwProcessId);
    }
}
