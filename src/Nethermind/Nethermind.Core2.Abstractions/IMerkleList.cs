using Nethermind.Core2.Crypto;

namespace Nethermind.Core2
{
    public interface IMerkleList
    {
        public uint Count { get; }
        
        public Root Root { get; }
    }
}