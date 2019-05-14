namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class BlockSyncBatch
    {
        public HeadersSyncBatch HeadersSyncBatch { get; set; }
        public BodiesSyncBatch BodiesSyncBatch { get; set; }
        public SyncPeerAllocation AssignedPeer { get; set; }
    }
}