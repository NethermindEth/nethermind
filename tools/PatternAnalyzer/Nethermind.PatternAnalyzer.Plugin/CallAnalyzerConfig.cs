namespace Nethermind.PatternAnalyzer.Plugin.Analyzer;

public class CallAnalyzerConfig : ICallAnalyzerConfig
{
    public bool Enabled { get; set; }
    public string? File { get; set; }
    public int WriteFrequency { get; set; } = 10;
    public int TopN { get; set; } = 100;
    public string ProcessingMode { get; set; } = "sequential";
    public string Sort { get; set; } = "ascending";
    public int ProcessingQueueSize { get; set; } = 100;
}
