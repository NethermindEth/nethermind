using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters
{
    public class FilterTopic
    {
        public Keccak First { get; set; }
        public Keccak Second { get; set; }
    }
}