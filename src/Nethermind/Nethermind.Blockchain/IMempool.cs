using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public interface IMempool
    {
        void AddPeer(ISynchronizationPeer peer);
        void RemovePeer(ISynchronizationPeer peer);
        void AddTransaction(Transaction transaction);
    }
}