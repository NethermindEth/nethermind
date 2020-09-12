namespace Nethermind.Trie.Pruning
{
    public interface IPersistenceStrategy
    {
        bool ShouldPersistSnapshot(long blocksSinceLastSnapshot);
    }
}