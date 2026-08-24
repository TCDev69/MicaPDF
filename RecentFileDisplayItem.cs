using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MicaPDF
{
    public sealed class RecentFileDisplayItem
    {
        public string Path { get; init; } = "";
        public string FileName { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public BitmapImage? CoverImage { get; init; }
        public Visibility HasCover => CoverImage != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoCover => CoverImage != null ? Visibility.Collapsed : Visibility.Visible;
    }
}
