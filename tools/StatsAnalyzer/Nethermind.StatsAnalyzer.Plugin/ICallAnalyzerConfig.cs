using Nethermind.Config;

namespace Nethermind.StatsAnalyzer.Plugin;

public interface ICallAnalyzerConfig : IConfig
{
    [ConfigItem(
        Description = "Activates or Deactivates Call Anayzer",
        DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(
        Description = "Sets the file to which the stats are dumped",
        DefaultValue = "null")]
    string? File { get; set; }

    [ConfigItem(
        Description = "Sets the block frequency for writing stats to disk",
        DefaultValue = "null")]
    int WriteFrequency { get; set; }

    [ConfigItem(Description = "Sets the number of top contracts called to track")]
    int TopN { get; set; }

    [ConfigItem(
        Description = "Sets the sort order of the stats produced",
        DefaultValue = "sequential")]
    string ProcessingMode { get; set; }

    [ConfigItem(
        Description = "Sets the sort order of the stats produced",
        DefaultValue = "unordered")]
    string Sort { get; set; }

    [ConfigItem(Description =
        "Sets the number of tasks that can be queued when tracing & dumping stats in background")]
    int ProcessingQueueSize { get; set; }
}
