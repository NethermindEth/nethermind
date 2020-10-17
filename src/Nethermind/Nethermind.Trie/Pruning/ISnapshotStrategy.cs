namespace Nethermind.Trie.Pruning
{
    public interface IPruningStrategy
    {
        bool ShouldPrune(in long blockNumber, in long highestBlockNumber, in long currentMemory);
        // TODO: I believe this is only thing we can use
        // bool ShouldPrune(in long lastPersisted, in long currentMemory);
    }
}