using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Input.Inking;

namespace MicaPDF
{
    public static class AnnotatedPdfExporter
    {
        /// <summary>
        /// Annotations are in Windows.Data.Pdf visual page space (DIPs, top-left, upright after /Rotate).
        /// PDFsharp 6.2 XGraphics uses MediaBox points with top-left origin and Y downwards;
        /// page /Rotate is applied by viewers, so we map visual → MediaBox drawing space.
        /// </summary>
        public static async Task ExportAsync(
            StorageFile sourcePdf,
            StorageFile destPdf,
            AnnotationStore store,
            IReadOnlyDictionary<uint, Size> winPageSizes)
        {
            EnsureFonts();

            var tempSrc = Path.Combine(Path.GetTempPath(), "micapdf-src-" + Guid.NewGuid().ToString("N") + ".pdf");
            var tempDst = Path.Combine(Path.GetTempPath(), "micapdf-out-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                using (var input = await sourcePdf.OpenStreamForReadAsync())
                using (var output = File.Create(tempSrc))
                    await input.CopyToAsync(output);

                PdfDocument source;
                try
                {
                    source = PdfReader.Open(tempSrc, PdfDocumentOpenMode.Import);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        Loc.Format("export.readError", ex.Message),
                        ex);
                }

                using (source)
                using (var document = new PdfDocument())
                {
                    for (var i = 0; i < source.PageCount; i++)
                    {
                        var page = document.AddPage(source.Pages[i]);
                        var pageIndex = (uint)i;

                        var media = page.MediaBoxReadOnly;
                        var crop = page.EffectiveCropBoxReadOnly;
                        var cropW = crop.Width;
                        var cropH = crop.Height;
                        var rotate = ((page.Rotate % 360) + 360) % 360;

                        // Visual upright size (what Windows.Data.Pdf shows).
                        var visW = rotate is 90 or 270 ? cropH : cropW;
                        var visH = rotate is 90 or 270 ? cropW : cropH;

                        winPageSizes.TryGetValue(pageIndex, out var winSize);
                        if (winSize.Width <= 1 || winSize.Height <= 1)
                            winSize = new Size(visW * 96.0 / 72.0, visH * 96.0 / 72.0);

                        var scaleX = visW / winSize.Width;
                        var scaleY = visH / winSize.Height;

                        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                        DrawStrokes(gfx, store.GetStrokes(pageIndex), media, crop, rotate, scaleX, scaleY);
                        DrawTexts(gfx, store.GetTexts(pageIndex), media, crop, rotate, scaleX, scaleY);
                    }

                    document.Save(tempDst);
                }

                using var destStream = await destPdf.OpenStreamForWriteAsync();
                destStream.SetLength(0);
                using var result = File.OpenRead(tempDst);
                await result.CopyToAsync(destStream);
            }
            finally
            {
                TryDelete(tempSrc);
                TryDelete(tempDst);
            }
        }

        /// <summary>
        /// Windows visual (top-left, upright, DIPs) → PDFsharp MediaBox drawing space (top-left, Y down).
        /// </summary>
        private static XPoint MapWinToGfx(
            double winX,
            double winY,
            PdfRectangle media,
            PdfRectangle crop,
            int rotate,
            double scaleX,
            double scaleY)
        {
            var vx = winX * scaleX; // visual points from crop top-left
            var vy = winY * scaleY;

            // PDF user space (bottom-left) within crop, then to PDFsharp top-left of MediaBox.
            // Corner mapping uses clockwise /Rotate of the physical page when displayed.
            double pdfX, pdfY;
            switch (rotate)
            {
                case 90: // visual TL = media BL
                    pdfX = crop.X1 + vy;
                    pdfY = crop.Y1 + vx;
                    break;
                case 180: // visual TL = media BR
                    pdfX = crop.X2 - vx;
                    pdfY = crop.Y1 + vy;
                    break;
                case 270: // visual TL = media TR
                    pdfX = crop.X2 - vy;
                    pdfY = crop.Y2 - vx;
                    break;
                default: // visual TL = media TL
                    pdfX = crop.X1 + vx;
                    pdfY = crop.Y2 - vy;
                    break;
            }

            // PDFsharp: (0,0) = MediaBox top-left, Y down.
            return new XPoint(pdfX - media.X1, media.Y2 - pdfY);
        }

        private static void DrawStrokes(
            XGraphics gfx,
            InkStrokeContainer container,
            PdfRectangle media,
            PdfRectangle crop,
            int rotate,
            double scaleX,
            double scaleY)
        {
            var scaleAvg = (scaleX + scaleY) / 2.0;
            foreach (var stroke in container.GetStrokes())
            {
                var segments = stroke.GetRenderingSegments();
                if (segments.Count < 2) continue;

                var attr = stroke.DrawingAttributes;
                var color = attr.Color;
                var alpha = attr.DrawAsHighlighter ? (byte)90 : color.A;
                var width = Math.Max(0.4, attr.Size.Width * scaleAvg);
                var pen = new XPen(XColor.FromArgb(alpha, color.R, color.G, color.B), width)
                {
                    LineCap = XLineCap.Round,
                    LineJoin = XLineJoin.Round
                };

                var path = new XGraphicsPath();
                var started = false;
                XPoint last = default;
                foreach (var seg in segments)
                {
                    var pos = MapWinToGfx(seg.Position.X, seg.Position.Y, media, crop, rotate, scaleX, scaleY);
                    if (!started)
                    {
                        last = pos;
                        started = true;
                    }
                    else
                    {
                        path.AddBezier(
                            last,
                            MapWinToGfx(seg.BezierControlPoint1.X, seg.BezierControlPoint1.Y, media, crop, rotate, scaleX, scaleY),
                            MapWinToGfx(seg.BezierControlPoint2.X, seg.BezierControlPoint2.Y, media, crop, rotate, scaleX, scaleY),
                            pos);
                        last = pos;
                    }
                }

                if (started)
                    gfx.DrawPath(pen, path);
            }
        }

        private static void DrawTexts(
            XGraphics gfx,
            List<TextAnnotation> texts,
            PdfRectangle media,
            PdfRectangle crop,
            int rotate,
            double scaleX,
            double scaleY)
        {
            var scaleAvg = (scaleX + scaleY) / 2.0;
            foreach (var text in texts)
            {
                if (string.IsNullOrWhiteSpace(text.Text)) continue;
                var style = XFontStyleEx.Regular;
                if (text.IsBold) style |= XFontStyleEx.Bold;
                if (text.IsItalic) style |= XFontStyleEx.Italic;
                var fontSize = Math.Max(4, text.FontSize * scaleAvg);
                var font = new XFont("Arial", fontSize, style);
                var brush = new XSolidBrush(XColor.FromArgb(text.Color.A, text.Color.R, text.Color.G, text.Color.B));
                var lines = text.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    var winY = text.Y + text.FontSize + i * (text.FontSize * 1.35);
                    var pt = MapWinToGfx(text.X, winY, media, crop, rotate, scaleX, scaleY);
                    // Glyphs must stay visually upright after the viewer applies /Rotate.
                    gfx.Save();
                    gfx.TranslateTransform(pt.X, pt.Y);
                    if (rotate != 0)
                        gfx.RotateTransform(-rotate);
                    gfx.DrawString(line, font, brush, 0, 0);
                    gfx.Restore();
                }
            }
        }

        private static void EnsureFonts()
        {
            try { GlobalFontSettings.UseWindowsFontsUnderWindows = true; }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
