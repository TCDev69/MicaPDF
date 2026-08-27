using System;
using System.IO;

namespace MicaPDF
{
    /// <summary>
    /// Paths and session helpers extracted from MainWindow for testability.
    /// </summary>
    public static class AppPaths
    {
        public static string RootDirectory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MicaPDF");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string LogsDirectory => Path.Combine(RootDirectory, "logs");
        public static string AnnotationsDirectory => Path.Combine(RootDirectory, "annotations");
        public static string TempDirectory => Path.Combine(RootDirectory, "temp");
    }

    /// <summary>
    /// Pure helpers for viewer session math (page clamps, zoom display).
    /// </summary>
    public static class ViewerSession
    {
        public static uint ClampPageIndex(uint pageIndex, uint pageCount)
        {
            if (pageCount == 0) return 0;
            return Math.Min(pageIndex, pageCount - 1);
        }

        public static double ClampZoom(double zoom, int maxZoomPercent = ZoomLimits.DefaultMaxZoomPercent) =>
            ZoomLimits.ClampZoom(zoom, maxZoomPercent);

        public static string FormatZoomPercent(double zoom) => $"{(zoom * 100):F0}%";
    }
}
