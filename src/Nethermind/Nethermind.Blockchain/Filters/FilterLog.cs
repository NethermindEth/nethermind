using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class FilterLog
    {
        public UInt256 LogIndex { get; }
        public UInt256 BlockNumber { get; }
        public Keccak BlockHash { get; }
        public UInt256 TransactionIndex { get; }
        public Keccak TransactionHash { get; }
        public Address Address { get; }
        public byte[] Data { get; }
        public Keccak[] Topics { get; }

        public FilterLog(UInt256 logIndex, UInt256 blockNumber, Keccak blockHash,
            UInt256 transactionIndex, Keccak transactionHash, 
            Address address, byte[] data, Keccak[] topics)
        {
            LogIndex = logIndex;
            BlockNumber = blockNumber;
            BlockHash = blockHash;
            TransactionIndex = transactionIndex;
            TransactionHash = transactionHash;
            Address = address;
            Data = data;
            Topics = topics;
        }
    }
}