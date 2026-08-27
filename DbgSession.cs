using System;
using System.IO;
using System.Text.Json;

namespace MicaPDF
{
    internal static class DbgSession
    {
        private static readonly string LogPath = @"c:\Users\TCDev\Documents\MicaPDF\debug-b9096f.log";

        public static void Log(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
        {
            // #region agent log
            try
            {
                var path = Path.GetFullPath(LogPath);
                var payload = JsonSerializer.Serialize(new
                {
                    sessionId = "b9096f",
                    runId,
                    hypothesisId,
                    location,
                    message,
                    data,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                File.AppendAllText(path, payload + Environment.NewLine);
            }
            catch { /* ignore */ }
            // #endregion
        }
    }
}
