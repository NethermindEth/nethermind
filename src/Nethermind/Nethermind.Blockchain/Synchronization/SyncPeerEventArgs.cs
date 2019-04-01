namespace Nethermind.Blockchain.Synchronization
{
    public class SyncPeerEventArgs
    {
        public SyncPeerEventArgs(ISynchronizationPeer syncPeer)
        {
            SyncPeer = syncPeer;
        }

        public ISynchronizationPeer SyncPeer { get; set; }
    }
}