using System;
using System.IO;
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
            var zoom = ZoomFitCalculator.Compute(100, 200, 400, 400, ZoomFitMode.Width, displayMultiplier: 2.0);
            Assert.Equal(2.0, zoom, 3);
        }

        [Fact]
        public void DisplayMultiplier_ScalesZoomDown()
        {
            // 125% DPI → multiplier 2.5 → lower zoom so Actual fits viewport
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
            var single = ZoomFitCalculator.Compute(100, 200, 400, 400, ZoomFitMode.Width, 2.0, false);
            var dual = ZoomFitCalculator.Compute(100, 200, 400, 400, ZoomFitMode.Width, 2.0, true);
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
}
