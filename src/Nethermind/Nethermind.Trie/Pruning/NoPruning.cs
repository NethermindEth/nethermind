namespace Nethermind.Trie.Pruning
{
    public class NoPruning : IPruningStrategy
    {
        private NoPruning()
        {
        }

        public static NoPruning Instance { get; } = new NoPruning();

        public bool ShouldPrune(in long blockNumber, in long highestBlockNumber, in long currentMemory)
        {
            return false;
        }
    }
}