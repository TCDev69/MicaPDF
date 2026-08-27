using System;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Pdf.IO;
using Windows.Data.Pdf;
using Windows.Storage;

namespace MicaPDF
{
    /// <summary>
    /// Opens password-protected PDFs by decrypting to a temp file for Windows.Data.Pdf.
    /// </summary>
    public static class PasswordPdfOpener
    {
        public static async Task<(PdfDocument? Document, StorageFile? WorkingFile, string? TempPath)> TryOpenAsync(
            StorageFile source,
            string? password)
        {
            try
            {
                var doc = await PdfDocument.LoadFromFileAsync(source);
                if (doc != null)
                    return (doc, source, null);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Direct PDF open failed: {ex.Message}");
            }

            if (string.IsNullOrEmpty(password))
                return (null, null, null);

            try
            {
                var bytes = await PdfPigServices.ReadBytesAsync(source);
                using var input = new MemoryStream(bytes);
                var pdf = PdfReader.Open(input, password, PdfDocumentOpenMode.Import);
                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MicaPDF",
                    "temp");
                Directory.CreateDirectory(tempDir);
                var tempPath = Path.Combine(tempDir, $"decrypted-{Guid.NewGuid():N}.pdf");
                pdf.Save(tempPath);
                pdf.Close();

                var tempFile = await StorageFile.GetFileFromPathAsync(tempPath);
                var doc = await PdfDocument.LoadFromFileAsync(tempFile);
                return (doc, tempFile, tempPath);
            }
            catch (Exception ex)
            {
                AppLog.Error("Password PDF open failed", ex);
                return (null, null, null);
            }
        }

        public static void TryDeleteTemp(string? tempPath)
        {
            if (string.IsNullOrEmpty(tempPath)) return;
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore
            }
        }
    }
}
