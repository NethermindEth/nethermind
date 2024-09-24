using Nethermind.Config;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;

namespace Nethermind.OpcodeStats.Plugin
{
    public interface IStatsConfig : IConfig
    {
        [ConfigItem(
            Description = "Activates or Deactivates OpcodeStats Plugin",
            DefaultValue = "true")]
        bool Enabled { get; set; }

        [ConfigItem(
            Description = "Sets the file to which the stats are dumped",
            DefaultValue = "null")]
        string? File { get; set; }

        [ConfigItem(Description = "Sets the size of the queue used to gather instructions per block")]
        int InstructionsQueueSize { get; set; }

        [ConfigItem(Description = "Sets the number of tasks that can be queued when tracing & dumping stats in background")]
        int ProcessingQueueSize { get; set; }

        [ConfigItem(
            Description = "Sets the number of buckets to use in CMSketch",
            DefaultValue = "null")]
        int? SketchBuckets { get; set; }

        [ConfigItem(
            Description = "Sets the number of hash functions to use in CMSketch",
            DefaultValue = "null")]
        int? SketchHashFunctions { get; set; }

        [ConfigItem(
            Description = "Sets the number of buckets derived from error to use in CMSketch",
            DefaultValue = "null")]
        double? SketchMaxError { get; set; }

        [ConfigItem(
            Description = "Sets the number of hash functions derived from min confidence  to use in CMSketch",
            DefaultValue = "null")]
        double? SketchMinConfidence { get; set; }

        [ConfigItem(Description = "Sets the number of top n-grams to track")]
        int AnalyzerTopN { get; set; }

        [ConfigItem(Description = "Sets the threshold for initial n-gram tracking")]
        ulong AnalyzerMinSupportThreshold { get; set; }

        [ConfigItem(Description = "Sets the capacity of filter used for n-gram tracking")]
        int AnalyzerCapacity { get; set; }

        [ConfigItem(Description = "Sets the buffer size for sketches used by stats analyzer")]
        int AnalyzerSketchBufferSize { get; set; }

        [ConfigItem(Description = "Sets the capacity of filter used for n-gram tracking")]
        double AnalyzerSketchResetOrReuseThreshold { get; set; }


        StatsAnalyzerConfig GetStatsAnalyzerConfig();

        CMSketchConfig GetSketchConfig();
    }
}
