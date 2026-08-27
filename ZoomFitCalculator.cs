using System;

namespace MicaPDF
{
    public enum ZoomFitMode
    {
        /// <summary>Entire page visible (fit height; letterboxing on sides OK).</summary>
        Height = 0,
        /// <summary>Page width fills viewport (may scroll vertically).</summary>
        Width = 1
    }

    /// <summary>Pure zoom-fit math (testable without WinUI).</summary>
    public static class ZoomFitCalculator
    {
        public const double ZoomStep = 0.25;

        /// <param name="pageWidthDip">Single page width in DIPs (Windows.Data.Pdf Size).</param>
        /// <param name="pageHeightDip">Single page height in DIPs.</param>
        /// <param name="availableWidth">Viewport content area width.</param>
        /// <param name="availableHeight">Viewport content area height.</param>
        /// <param name="displayMultiplier">
        /// Actual on-screen size / (pageDip * zoom). Typically 2 * DPI scale
        /// (render uses zoom*2, WinUI may layout bitmap pixels as DIPs × scale).
        /// </param>
        /// <param name="doublePage">When true, treat width as two pages side by side.</param>
        public static double Compute(
            double pageWidthDip,
            double pageHeightDip,
            double availableWidth,
            double availableHeight,
            ZoomFitMode mode,
            double displayMultiplier = 2.0,
            bool doublePage = false)
        {
            if (pageWidthDip <= 0 || pageHeightDip <= 0) return 0.5;
            if (availableWidth <= 0 || availableHeight <= 0) return 0.5;
            if (displayMultiplier < 0.5) displayMultiplier = 2.0;

            var layoutWidth = doublePage ? pageWidthDip * 2 : pageWidthDip;
            var zoomX = availableWidth / (layoutWidth * displayMultiplier);
            var zoomY = availableHeight / (pageHeightDip * displayMultiplier);

            var zoom = mode == ZoomFitMode.Height ? zoomY : zoomX;
            return Math.Clamp(zoom, 0.1, 5.0);
        }

        public static ZoomFitMode NextMode(ZoomFitMode current) =>
            current == ZoomFitMode.Height ? ZoomFitMode.Width : ZoomFitMode.Height;

        /// <summary>Snap to 25% grid for expensive re-rasterization.</summary>
        public static double SnapToStep(double zoom, double step = ZoomStep)
        {
            if (step <= 0) return zoom;
            var snapped = Math.Round(zoom / step) * step;
            return Math.Clamp(snapped, 0.25, 5.0);
        }
    }
}
