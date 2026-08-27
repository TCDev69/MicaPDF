using System;

namespace MicaPDF
{
    public static class ZoomLimits
    {
        public const int DefaultMaxZoomPercent = 150;
        public const int MinMaxZoomPercent = 50;
        public const int MaxMaxZoomPercent = 500;

        public static int SanitizeMaxZoomPercent(int percent) =>
            Math.Clamp(percent, MinMaxZoomPercent, MaxMaxZoomPercent);

        public static double MaxZoomFromPercent(int percent) =>
            SanitizeMaxZoomPercent(percent) / 100.0;

        public static double ClampZoom(double zoom, int maxZoomPercent) =>
            Math.Clamp(zoom, 0.25, MaxZoomFromPercent(maxZoomPercent));
    }
}
