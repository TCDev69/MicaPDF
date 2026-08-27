using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MicaPDF
{
    /// <summary>Load-path timing breakdown logged via <see cref="AppLog"/>.</summary>
    public sealed class LoadDiagnostics : IDisposable
    {
        private static readonly object Gate = new();
        private static readonly List<(string Name, long Ms)> Steps = new();
        private static readonly Stopwatch Total = Stopwatch.StartNew();

        private readonly string _step;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        private LoadDiagnostics(string step) => _step = step;

        public static LoadDiagnostics BeginLoad(string fileName, long fileBytes, uint pageCount)
        {
            lock (Gate)
            {
                Steps.Clear();
                Total.Restart();
            }

            AppLog.Info($"Load start: {fileName} ({fileBytes / (1024 * 1024)} MB, {pageCount} pages)");
            return new LoadDiagnostics("start");
        }

        public static LoadDiagnostics Step(string name) => new(name);

        public void Dispose()
        {
            _sw.Stop();
            lock (Gate)
            {
                Steps.Add((_step, _sw.ElapsedMilliseconds));
            }
        }

        public static void Complete(long workingSetMb = 0)
        {
            lock (Gate)
            {
                var sb = new StringBuilder();
                sb.Append($"Load complete {Total.ElapsedMilliseconds} ms");
                if (workingSetMb > 0)
                    sb.Append($", WS ~{workingSetMb} MB");
                sb.Append(" [");
                for (var i = 0; i < Steps.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Steps[i].Name).Append('=').Append(Steps[i].Ms).Append("ms");
                }

                sb.Append(']');
                AppLog.Info(sb.ToString());
            }
        }

        public static long GetWorkingSetMb()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                return p.WorkingSet64 / (1024 * 1024);
            }
            catch
            {
                return 0;
            }
        }
    }
}
