using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MicaPDF
{
    public enum AppLogLevel
    {
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// Rotating file logger under %LocalAppData%\MicaPDF\logs (max 3 files).
    /// </summary>
    public static class AppLog
    {
        private static readonly object Gate = new();
        private static StreamWriter? _writer;
        private static string? _currentPath;
        private const int MaxFiles = 3;

        public static string LogsDirectory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MicaPDF",
                    "logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static void Initialize()
        {
            lock (Gate)
            {
                try
                {
                    TrimOldLogs(LogsDirectory);
                    var path = Path.Combine(LogsDirectory, $"mica-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                    _currentPath = path;
                    WriteUnlocked(AppLogLevel.Info, "App started");
                }
                catch
                {
                    _writer = null;
                    _currentPath = null;
                }
            }
        }

        public static void Info(string message) => Write(AppLogLevel.Info, message);

        public static void Warn(string message) => Write(AppLogLevel.Warn, message);

        public static void Error(string message, Exception? ex = null)
        {
            if (ex == null)
                Write(AppLogLevel.Error, message);
            else
                Write(AppLogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        public static void Write(AppLogLevel level, string message)
        {
            lock (Gate)
            {
                WriteUnlocked(level, message);
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                try
                {
                    WriteUnlocked(AppLogLevel.Info, "App shutting down");
                    _writer?.Flush();
                    _writer?.Dispose();
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    _writer = null;
                    _currentPath = null;
                }
            }
        }

        /// <summary>Keeps at most <paramref name="maxFiles"/> newest *.log files. Exposed for tests.</summary>
        public static void TrimOldLogs(string directory, int maxFiles = MaxFiles)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                var files = Directory.GetFiles(directory, "mica-*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .ToList();

                for (var i = maxFiles; i < files.Count; i++)
                {
                    try { files[i].Delete(); } catch { /* ignore */ }
                }
            }
            catch
            {
                // ignore
            }
        }

        private static void WriteUnlocked(AppLogLevel level, string message)
        {
            if (_writer == null) return;
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t{level}\t{message}";
                _writer.WriteLine(line);
                if (level == AppLogLevel.Error)
                    _writer.Flush();
            }
            catch
            {
                // ignore logging failures
            }
        }
    }
}
