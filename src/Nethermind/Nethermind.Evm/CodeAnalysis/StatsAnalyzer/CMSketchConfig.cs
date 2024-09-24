namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{
    public class CMSketchConfig
    {
        public int? Buckets { get; set; }
        public int? HashFunctions { get; set; }
        public double? MinConfidence { get; set; }
        public double? MaxError { get; set; }
    }
}
