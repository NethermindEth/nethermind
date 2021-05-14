using Nethermind.Config;

namespace Nethermind.Dsl
{
    public interface IDslConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether the DSL is enabled on node startup.",
            DefaultValue = "false")]
        bool Enabled { get; set; } 
    }
}