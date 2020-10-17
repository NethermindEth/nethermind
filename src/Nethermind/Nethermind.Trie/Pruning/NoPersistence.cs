namespace Nethermind.Trie.Pruning
{
    public class NoPersistence : IPersistenceStrategy
    {
        private NoPersistence()
        {
        }

        public static NoPersistence Instance { get; } = new NoPersistence();

        public bool ShouldPersist(long blockNumber)
        {
            return false;
        }
    }
}