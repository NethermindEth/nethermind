using Nethermind.Config;
using Nethermind.Evm;

namespace Nethermind.Search.Plugin
{
    public class SearchConfig : ISearchConfig
    {
        public bool Enabled { get; set; }
        public string? File { get; set; }
    }
}
