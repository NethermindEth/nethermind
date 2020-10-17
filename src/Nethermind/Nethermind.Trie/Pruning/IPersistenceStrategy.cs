namespace Nethermind.Trie.Pruning
{
    public interface IPersistenceStrategy
    {
        bool ShouldPersist(long blockNumber);
    }
}