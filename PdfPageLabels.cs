using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Windows.Storage;

namespace MicaPDF
{
    /// <summary>
    /// Logical page labels from the PDF catalog /PageLabels number tree (ISO 32000 §12.4.2).
    /// </summary>
    public sealed class PdfPageLabels
    {
        private readonly string[] _labels;
        private readonly bool _isIdentity;

        private PdfPageLabels(string[] labels, bool isIdentity)
        {
            _labels = labels;
            _isIdentity = isIdentity;
        }

        public int Count => _labels.Length;
        public bool IsIdentity => _isIdentity;

        public string GetLabel(uint pageIndex)
        {
            if (pageIndex >= (uint)_labels.Length)
                return (pageIndex + 1).ToString();
            return _labels[pageIndex];
        }

        public bool TryFindPageIndex(string label, out uint pageIndex)
        {
            pageIndex = 0;
            if (string.IsNullOrWhiteSpace(label))
                return false;

            for (var i = 0; i < _labels.Length; i++)
            {
                if (string.Equals(_labels[i], label, StringComparison.OrdinalIgnoreCase))
                {
                    pageIndex = (uint)i;
                    return true;
                }
            }

            return false;
        }

        public static async Task<PdfPageLabels?> LoadAsync(StorageFile file, uint pageCount)
        {
            if (pageCount == 0)
                return null;

            try
            {
                var temp = Path.Combine(Path.GetTempPath(), "micapdf-labels-" + Guid.NewGuid().ToString("N") + ".pdf");
                try
                {
                    using (var input = await file.OpenStreamForReadAsync())
                    using (var output = File.Create(temp))
                        await input.CopyToAsync(output);

                    return LoadFromPath(temp, (int)pageCount);
                }
                finally
                {
                    try { File.Delete(temp); } catch { /* ignore */ }
                }
            }
            catch
            {
                return null;
            }
        }

        public static PdfPageLabels? LoadFromPath(string path, int pageCount)
        {
            if (pageCount <= 0)
                return null;

            try
            {
                using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                var catalog = document.Internals.Catalog;
                var pageLabelsDict = catalog.Elements.GetDictionary("/PageLabels");
                if (pageLabelsDict == null)
                    return null;

                var ranges = new List<(int StartIndex, LabelStyle Style, string Prefix, int StartNumber)>();
                CollectRanges(pageLabelsDict, ranges);
                if (ranges.Count == 0)
                    return null;

                ranges.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));

                var labels = new string[pageCount];
                var rangeIdx = 0;
                for (var i = 0; i < pageCount; i++)
                {
                    while (rangeIdx + 1 < ranges.Count && ranges[rangeIdx + 1].StartIndex <= i)
                        rangeIdx++;

                    if (i < ranges[0].StartIndex)
                    {
                        labels[i] = (i + 1).ToString();
                        continue;
                    }

                    var applicable = ranges[rangeIdx];
                    if (i < applicable.StartIndex)
                    {
                        labels[i] = (i + 1).ToString();
                        continue;
                    }

                    var value = applicable.StartNumber + (i - applicable.StartIndex);
                    labels[i] = FormatLabel(applicable.Style, applicable.Prefix, value);
                }

                var identity = true;
                for (var i = 0; i < pageCount; i++)
                {
                    if (!string.Equals(labels[i], (i + 1).ToString(), StringComparison.Ordinal))
                    {
                        identity = false;
                        break;
                    }
                }

                return new PdfPageLabels(labels, identity);
            }
            catch
            {
                return null;
            }
        }

        private enum LabelStyle
        {
            None,
            Decimal,
            UpperRoman,
            LowerRoman,
            UpperLetters,
            LowerLetters
        }

        private static void CollectRanges(PdfDictionary node, List<(int StartIndex, LabelStyle Style, string Prefix, int StartNumber)> ranges)
        {
            var nums = node.Elements.GetArray("/Nums");
            if (nums != null)
            {
                for (var i = 0; i + 1 < nums.Elements.Count; i += 2)
                {
                    int startIndex;
                    try { startIndex = nums.Elements.GetInteger(i); }
                    catch { continue; }

                    var dict = nums.Elements.GetDictionary(i + 1);
                    if (dict == null)
                        continue;

                    ranges.Add(ParseLabelDict(startIndex, dict));
                }
            }

            var kids = node.Elements.GetArray("/Kids");
            if (kids == null)
                return;

            for (var i = 0; i < kids.Elements.Count; i++)
            {
                var kid = kids.Elements.GetDictionary(i);
                if (kid != null)
                    CollectRanges(kid, ranges);
            }
        }

        private static (int StartIndex, LabelStyle Style, string Prefix, int StartNumber) ParseLabelDict(int startIndex, PdfDictionary dict)
        {
            var style = LabelStyle.None;
            var styleName = dict.Elements.GetName("/S");
            if (!string.IsNullOrEmpty(styleName))
            {
                style = styleName.TrimStart('/') switch
                {
                    "D" => LabelStyle.Decimal,
                    "R" => LabelStyle.UpperRoman,
                    "r" => LabelStyle.LowerRoman,
                    "A" => LabelStyle.UpperLetters,
                    "a" => LabelStyle.LowerLetters,
                    _ => LabelStyle.None
                };
            }

            var prefix = dict.Elements.GetString("/P") ?? "";
            var startNumber = 1;
            if (dict.Elements.ContainsKey("/St"))
            {
                try { startNumber = Math.Max(1, dict.Elements.GetInteger("/St")); }
                catch { startNumber = 1; }
            }

            return (startIndex, style, prefix, startNumber);
        }

        private static string FormatLabel(LabelStyle style, string prefix, int value)
        {
            var numeric = style switch
            {
                LabelStyle.Decimal => value.ToString(),
                LabelStyle.UpperRoman => ToRoman(value, upper: true),
                LabelStyle.LowerRoman => ToRoman(value, upper: false),
                LabelStyle.UpperLetters => ToLetters(value, upper: true),
                LabelStyle.LowerLetters => ToLetters(value, upper: false),
                _ => ""
            };

            return prefix + numeric;
        }

        private static string ToRoman(int value, bool upper)
        {
            if (value <= 0)
                return "";

            // Cap to avoid huge strings on corrupt /St values.
            value = Math.Min(value, 3999);

            var map = new (int N, string U, string L)[]
            {
                (1000, "M", "m"), (900, "CM", "cm"), (500, "D", "d"), (400, "CD", "cd"),
                (100, "C", "c"), (90, "XC", "xc"), (50, "L", "l"), (40, "XL", "xl"),
                (10, "X", "x"), (9, "IX", "ix"), (5, "V", "v"), (4, "IV", "iv"), (1, "I", "i")
            };

            var sb = new System.Text.StringBuilder();
            foreach (var (n, u, l) in map)
            {
                while (value >= n)
                {
                    sb.Append(upper ? u : l);
                    value -= n;
                }
            }

            return sb.ToString();
        }

        private static string ToLetters(int value, bool upper)
        {
            if (value <= 0)
                return "";

            // A..Z, AA..ZZ, … as per PDF spec.
            value = Math.Min(value, 26 * 26 * 26);
            var sb = new System.Text.StringBuilder();
            while (value > 0)
            {
                value--;
                var c = (char)((upper ? 'A' : 'a') + (value % 26));
                sb.Insert(0, c);
                value /= 26;
            }

            return sb.ToString();
        }
    }
}
