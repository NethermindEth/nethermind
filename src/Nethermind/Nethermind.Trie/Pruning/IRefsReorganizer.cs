using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface IRefsReorganizer
    {
        void MoveBack(params Keccak[] hashes);
    }
}