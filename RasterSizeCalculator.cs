using System;

namespace MicaPDF
{
    public static class RasterSizeCalculator
    {
        /// <summary>Hard cap on raster dimension; keeps large scans from allocating multi-hundred-MB bitmaps.</summary>
        public const int MaxPixelDimension = 5120;

        /// <summary>Quality multiplier over layout DIPs; 1.5× scales with HiDPI (was 2.0).</summary>
        public static double RasterMultiplier(double rasterizationScale) =>
            1.5 * Math.Max(1.0, rasterizationScale);

        public static (uint Width, uint Height) ComputeDestinationSize(
            double pageWidthDip,
            double pageHeightDip,
            double zoom,
            double rasterizationScale,
            double viewportWidthDip = 0,
            double viewportHeightDip = 0)
        {
            var mul = RasterMultiplier(rasterizationScale);
            var w = pageWidthDip * zoom * mul;
            var h = pageHeightDip * zoom * mul;
            w = ApplyViewportSoftCap(w, viewportWidthDip, zoom, mul);
            h = ApplyViewportSoftCap(h, viewportHeightDip, zoom, mul);
            return CapDimensions(w, h);
        }

        public static (uint Width, uint Height) ComputeDisplaySize(
            double pageWidthDip,
            double pageHeightDip,
            double zoom,
            double rasterizationScale,
            double viewportWidthDip = 0,
            double viewportHeightDip = 0)
        {
            var mul = RasterMultiplier(rasterizationScale);
            var w = pageWidthDip * zoom * mul;
            var h = pageHeightDip * zoom * mul;
            w = ApplyViewportSoftCap(w, viewportWidthDip, zoom, mul);
            h = ApplyViewportSoftCap(h, viewportHeightDip, zoom, mul);
            return CapDimensions(w, h);
        }

        /// <summary>Don't raster more than ~1.2× viewport edge at current zoom (large scrollable pages).</summary>
        private static double ApplyViewportSoftCap(double pixels, double viewportDip, double zoom, double mul)
        {
            if (viewportDip < 64 || zoom < 0.01) return pixels;
            var cap = viewportDip * zoom * mul * 1.2;
            return Math.Min(pixels, Math.Max(cap, 1024));
        }

        public static (uint Width, uint Height) CapDimensions(double width, double height)
        {
            var w = Math.Max(1, width);
            var h = Math.Max(1, height);
            var max = Math.Max(w, h);
            if (max <= MaxPixelDimension)
                return ((uint)w, (uint)h);

            var scale = MaxPixelDimension / max;
            return ((uint)Math.Max(1, w * scale), (uint)Math.Max(1, h * scale));
        }

        public static long EstimateRgbaBytes(uint width, uint height) =>
            (long)width * height * 4;
    }
}
