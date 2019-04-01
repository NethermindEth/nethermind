using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Stats.Model;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISynchronizationServer
    {
        void HintBlock(Keccak hash, UInt256 number, Node receivedFrom);
        void AddNewBlock(Block block, Node node);
        TransactionReceipt[][] GetReceipts(Keccak[] blockHashes);
        Block Find(Keccak hash);
        Block Find(UInt256 number);
        Block[] Find(Keccak hash, int numberOfBlocks, int skip, bool reverse);
        byte[][] GetNodeData(Keccak[] keys);
    }
}