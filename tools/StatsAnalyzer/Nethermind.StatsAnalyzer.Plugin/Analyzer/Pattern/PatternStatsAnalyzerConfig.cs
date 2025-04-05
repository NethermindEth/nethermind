namespace Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;

public class PatternStatsAnalyzerConfig
{
    public required CmSketchConfig Sketch { get; set; }
    public int TopN { get; set; }
    public ulong MinSupport { get; set; }
    public int Capacity { get; set; }
    public int BufferSizeForSketches { get; set; }
    public double SketchResetOrReuseThreshold { get; set; }
}
