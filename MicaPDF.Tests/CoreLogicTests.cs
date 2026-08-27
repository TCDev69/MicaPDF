using System;
using System.Collections.Generic;
using System.IO;
using Windows.Foundation;
using Xunit;

namespace MicaPDF.Tests
{
    public class ZoomFitCalculatorTests
    {
        [Fact]
        public void HeightFit_UsesVerticalZoom()
        {
            var zoom = ZoomFitCalculator.Compute(100, 200, 400, 400, ZoomFitMode.Height, displayMultiplier: 2.0);
            Assert.Equal(1.0, zoom, 3);
        }

        [Fact]
        public void WidthFit_UsesHorizontalZoom()
        {
            var zoom = ZoomFitCalculator.Compute(100, 200, 400, 400, ZoomFitMode.Width, displayMultiplier: 2.0, maxZoomPercent: 500);
            Assert.Equal(2.0, zoom, 3);
        }

        [Fact]
        public void DisplayMultiplier_ScalesZoomDown()
        {
            var zoom = ZoomFitCalculator.Compute(100, 200, 400, 400, ZoomFitMode.Height, displayMultiplier: 2.5);
            Assert.Equal(0.8, zoom, 3);
        }

        [Fact]
        public void NextMode_Toggles()
        {
            Assert.Equal(ZoomFitMode.Width, ZoomFitCalculator.NextMode(ZoomFitMode.Height));
            Assert.Equal(ZoomFitMode.Height, ZoomFitCalculator.NextMode(ZoomFitMode.Width));
        }

        [Fact]
        public void DoublePage_WidensLayoutForWidthFit()
        {
            var single = ZoomFitCalculator.Compute(100, 200, 400, 400, ZoomFitMode.Width, 2.0, false, 500);
            var dual = ZoomFitCalculator.Compute(100, 200, 400, 400, ZoomFitMode.Width, 2.0, true, 500);
            Assert.True(dual < single);
            Assert.Equal(1.0, dual, 3);
        }

        [Fact]
        public void SnapToStep_RoundsTo25Percent()
        {
            Assert.Equal(0.5, ZoomFitCalculator.SnapToStep(0.51), 3);
            Assert.Equal(0.5, ZoomFitCalculator.SnapToStep(0.62), 3);
            Assert.Equal(0.75, ZoomFitCalculator.SnapToStep(0.63), 3);
            Assert.Equal(0.25, ZoomFitCalculator.SnapToStep(0.2), 3);
        }
    }

    public class AppLogTrimTests
    {
        [Fact]
        public void TrimOldLogs_KeepsOnlyMaxFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "MicaPDF-log-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                for (var i = 0; i < 5; i++)
                {
                    var path = Path.Combine(dir, $"mica-2020010{i}-120000.log");
                    File.WriteAllText(path, "x");
                    File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-i));
                }

                AppLog.TrimOldLogs(dir, maxFiles: 3);
                Assert.Equal(3, Directory.GetFiles(dir, "mica-*.log").Length);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }
    }

    public class ViewerSessionTests
    {
        [Fact]
        public void ClampPageIndex_RespectsBounds()
        {
            Assert.Equal(0u, ViewerSession.ClampPageIndex(5, 0));
            Assert.Equal(4u, ViewerSession.ClampPageIndex(99, 5));
            Assert.Equal(2u, ViewerSession.ClampPageIndex(2, 5));
        }

        [Fact]
        public void FormatZoomPercent_Rounds()
        {
            Assert.Equal("75%", ViewerSession.FormatZoomPercent(0.75));
        }

        [Fact]
        public void ClampZoom_RespectsMaxPercent()
        {
            Assert.Equal(1.5, ViewerSession.ClampZoom(3.0, 150), 3);
            Assert.Equal(0.5, ViewerSession.ClampZoom(0.5, 150), 3);
            Assert.Equal(0.25, ViewerSession.ClampZoom(0.1, 150), 3);
        }
    }

    public class AnnotationKeyTests
    {
        [Fact]
        public void KeyFor_IsStableAndLength32()
        {
            var a = AnnotationKeys.KeyFor(@"C:\Docs\a.pdf", 1234);
            var b = AnnotationKeys.KeyFor(@"c:\docs\a.pdf", 1234);
            var c = AnnotationKeys.KeyFor(@"C:\Docs\a.pdf", 9999);
            Assert.Equal(32, a.Length);
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }
    }

    public class RasterSizeCalculatorTests
    {
        [Fact]
        public void CapDimensions_LimitsLongestSideTo5120()
        {
            var (w, h) = RasterSizeCalculator.CapDimensions(12000, 9000);
            Assert.Equal(5120u, w);
            Assert.Equal(3840u, h);
        }

        [Fact]
        public void ComputeDestinationSize_UsesRasterizationScale()
        {
            var (w, h) = RasterSizeCalculator.ComputeDestinationSize(816, 1056, 0.48, 1.0);
            Assert.True(w < 2000);
            Assert.True(h < 2500);
            Assert.True(Math.Max(w, h) <= RasterSizeCalculator.MaxPixelDimension);
        }

        [Fact]
        public void EstimateRgbaBytes_MatchesDimensions()
        {
            Assert.Equal(4L * 100 * 100, RasterSizeCalculator.EstimateRgbaBytes(100, 100));
        }
    }

    public class PdfPageCachePolicyTests
    {
        [Fact]
        public void DefaultTrimDistance_Is8()
        {
            Assert.Equal(8, PdfPageCache.DefaultTrimDistance);
        }

        [Fact]
        public void DefaultByteBudget_Is80Mb()
        {
            Assert.Equal(80L * 1024 * 1024, PdfPageCache.DefaultByteBudget);
        }
    }

    public class LazyPdfTextIndexTests
    {
        [Fact]
        public void CreateLazy_StartsWithoutIndexedPages()
        {
            var sizes = new Dictionary<uint, Size> { [0] = new Size(100, 100) };
            using var index = PdfTextIndex.CreateLazy(@"C:\missing.pdf", sizes);
            Assert.Empty(index.PageIndices);
            Assert.Equal(0, index.IndexedPageCount);
        }

        [Fact]
        public void LoadFromBytes_HandlesEmptyBytes()
        {
            var sizes = new Dictionary<uint, Size> { [0] = new Size(100, 100) };
            using var index = PdfTextIndex.LoadFromBytes(Array.Empty<byte>(), sizes);
            Assert.Equal(0, index.IndexedPageCount);
        }
    }
}
