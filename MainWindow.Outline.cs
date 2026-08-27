using System;
using System.Threading;
using System.Threading.Tasks;

namespace MicaPDF
{
    public sealed partial class MainWindow
    {
        private async Task EnsureOutlineLoadedAsync()
        {
            if (_pdfOutline != null || string.IsNullOrEmpty(_textIndexSourcePath))
                return;

            _outlineLoadCts?.Cancel();
            _outlineLoadCts?.Dispose();
            _outlineLoadCts = new CancellationTokenSource();
            var token = _outlineLoadCts.Token;
            var path = _textIndexSourcePath;

            try
            {
                var outline = await Task.Run(() => PdfPigServices.LoadOutlineFromPath(path), token);
                if (token.IsCancellationRequested) return;
                _pdfOutline = outline;
                PopulateOutlineTree();
                ApplyOutlinePaneState();
                RefreshOutlineEmptyState();
                // #region agent log
                DbgSession.Log("H6", "MainWindow.EnsureOutlineLoadedAsync", "outline loaded",
                    new { wsMb = LoadDiagnostics.GetWorkingSetMb(), hasOutline = outline?.HasEntries == true });
                // #endregion
            }
            catch (OperationCanceledException)
            {
                // Document replaced.
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Outline load failed: {ex.Message}");
            }
        }
    }
}
