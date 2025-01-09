using Nethermind.Config;
using Nethermind.Evm;

namespace Nethermind.Search.Plugin
{
    public interface ISearchConfig : IConfig
    {
           [ConfigItem(
            Description = "Activates or Deactivates Search Plugin",
            DefaultValue = "false")]
        bool Enabled { get; set; }

        [ConfigItem(
            Description = "Sets the file to which the search results are dumped",
            DefaultValue = "null")]
        string? File { get; set; }
    }
}
