namespace Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;

public class CmSketchConfig
{
    public int? Buckets { get; set; }
    public int? HashFunctions { get; set; }
    public double? MinConfidence { get; set; }
    public double? MaxError { get; set; }
}
