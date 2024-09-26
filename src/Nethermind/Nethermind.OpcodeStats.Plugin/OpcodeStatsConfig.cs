using Nethermind.Config;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
namespace Nethermind.OpcodeStats.Plugin
{
    public class StatsConfig : IStatsConfig
    {
        public bool Enabled { get; set; }
        public string? File { get; set; }
        public int WriteFrequency { get; set; } = 10;
        public string Ignore { get; set; } = "";
        public int InstructionsQueueSize { get; set; }
        public int ProcessingQueueSize { get; set; }
        public int? SketchBuckets { get; set; }
        public int? SketchHashFunctions { get; set; }
        public double? SketchMaxError { get; set; }
        public double? SketchMinConfidence { get; set; }
        public int AnalyzerTopN { get; set; }
        public ulong AnalyzerMinSupportThreshold { get; set; }
        public int AnalyzerCapacity { get; set; }
        public int AnalyzerSketchBufferSize { get; set; }
        public double AnalyzerSketchResetOrReuseThreshold { get; set; }

        public StatsAnalyzerConfig GetStatsAnalyzerConfig()
        {
            StatsAnalyzerConfig config = new StatsAnalyzerConfig
            {
                Sketch = GetSketchConfig(),
                TopN = AnalyzerTopN,
                MinSupport = AnalyzerMinSupportThreshold,
                Capacity = AnalyzerCapacity,
                BufferSizeForSketches = AnalyzerSketchBufferSize,
                SketchResetOrReuseThreshold = AnalyzerSketchResetOrReuseThreshold,
            };
            return config;
        }

        public CMSketchConfig GetSketchConfig()
        {
            CMSketchConfig config = new CMSketchConfig
            {
                Buckets = SketchBuckets,
                HashFunctions = SketchHashFunctions,
                MinConfidence = SketchMinConfidence,
                MaxError = SketchMaxError
            };
            return config;
        }

        public HashSet<Instruction> GetIgnoreSet()
        {
            var ignoreSet = new HashSet<Instruction>();
            string[] deserializedArray = Ignore.Split(',').Select(item => item.Trim()).ToArray();
            foreach (var instructionString in deserializedArray)
            {
               var instruction = (Instruction)Enum.Parse(typeof(Instruction), instructionString);
               ignoreSet.Add(instruction);
            }

            return ignoreSet;
        }
    }
}
