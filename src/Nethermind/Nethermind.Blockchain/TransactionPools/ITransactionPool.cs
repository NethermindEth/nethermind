using Nethermind.Core;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionPool
    {
        void AddPeer(ISynchronizationPeer peer);
        void RemovePeer(ISynchronizationPeer peer);
        void AddTransaction(Transaction transaction);
        void UpdateTransaction(Transaction transaction);
    }
}