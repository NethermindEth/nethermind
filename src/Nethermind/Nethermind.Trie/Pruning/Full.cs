namespace Nethermind.Trie.Pruning
{
    public static class Full
    {
        public static IPersistenceStrategy Archive = new ConstantInterval(1);
    }
}