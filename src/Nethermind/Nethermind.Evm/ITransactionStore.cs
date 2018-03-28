using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm
{
    public interface ITransactionStore
    {
        void AddTransaction(Transaction transaction);
        void AddTransactionReceipt(Keccak transactionHash, TransactionReceipt transactionReceipt, Keccak blockHash);
        Transaction GetTransaction(Keccak transactionHash);
        TransactionReceipt GetTransactionReceipt(Keccak transactionHash);
        bool WasProcessed(Keccak transactionHash);
        ///get hash of the block transaction was in
        Keccak GetBlockHash(Keccak transactionHash);
    }
}