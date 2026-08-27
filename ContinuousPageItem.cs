namespace MicaPDF
{
    public sealed class ContinuousPageItem
    {
        public uint PageIndex { get; init; }
        public double DisplayWidth { get; init; }
        public double DisplayHeight { get; init; }
        public double PageWidthDip { get; init; }
        public double PageHeightDip { get; init; }
        public double LayoutHeight => DisplayHeight + 16;
    }
}
