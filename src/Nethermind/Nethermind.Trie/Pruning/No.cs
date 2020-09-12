namespace Nethermind.Trie.Pruning
{
    public class No
    {
        public static IPersistenceStrategy Persistence => NoPersistence.Instance;
        public static IPruningStrategy Pruning => NoPruning.Instance;
    }
}