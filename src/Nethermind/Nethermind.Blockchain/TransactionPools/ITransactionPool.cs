using Nethermind.Core;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionPool
    {
        void AddFilter<T>(T filter) where T : ITransactionPoolFilter;
        void DeleteFilter<T>() where T : ITransactionPoolFilter;
        void AddStorage<T>(T storage) where T : ITransactionPoolStorage;
        void DeleteStorage<T>() where T : ITransactionPoolStorage;
        void AddPeer(ISynchronizationPeer peer);
        void RemovePeer(ISynchronizationPeer peer);
        void TryAddTransaction(Transaction transaction);
        void TryDeleteTransaction(Transaction transaction);
    }
}