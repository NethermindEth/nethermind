using Nethermind.Config;

namespace Nethermind.Merge.AuRa
{
    public interface IAuRaMergeConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether the AuRa Merge plugin variant is enabled.",
            DefaultValue = "false")]
        bool Enabled { get; set; }
    }
}
