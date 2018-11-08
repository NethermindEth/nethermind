using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionPool
    {
        Transaction[] PendingTransactions { get; }
        TransactionReceipt GetReceipt(Keccak hash);
        void AddFilter<T>(T filter) where T : ITransactionFilter;
        void DeleteFilter<T>() where T : ITransactionFilter;
        void AddPeer(ISynchronizationPeer peer);
        void DeletePeer(NodeId nodeId);
        void AddTransaction(Transaction transaction, UInt256 blockNumber);
        void DeleteTransaction(Keccak hash);
        void AddReceipt(TransactionReceipt receipt);
        event EventHandler<TransactionEventArgs> NewPending;
    }
}