namespace Nethermind.Trie.Pruning
{
    public interface IPruningStrategy
    {
        bool ShouldPrune(in long blockNumber, in long highestBlockNumber, in long currentMemory);
    }
}