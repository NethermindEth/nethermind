namespace Nethermind.Trie.Pruning
{
    public class ConstantInterval : IPersistenceStrategy
    {
        private readonly long _snapshotInterval;

        public ConstantInterval(long snapshotInterval)
        {
            _snapshotInterval = snapshotInterval;
        }
        
        public bool ShouldPersistSnapshot(long blockNumber)
        {
            return blockNumber% _snapshotInterval == 0;
        }
    }
}