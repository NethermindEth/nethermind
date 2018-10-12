using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core
{
    public class TransactionReceiptContext
    {
        public TransactionReceipt Receipt { get; }
        public UInt256 LogIndex { get; }
        public UInt256 BlockNumber { get; }
        public Keccak BlockHash { get; }
        public UInt256 TransactionIndex { get; }
        public Keccak TransactionHash { get; }
        
        public TransactionReceiptContext(TransactionReceipt receipt, UInt256 logIndex, UInt256 blockNumber, 
            Keccak blockHash, UInt256 transactionIndex, Keccak transactionHash)
        {
            Receipt = receipt;
            LogIndex = logIndex;
            BlockNumber = blockNumber;
            BlockHash = blockHash;
            TransactionIndex = transactionIndex;
            TransactionHash = transactionHash;
        }
    }
}