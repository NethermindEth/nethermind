namespace Nethermind.Trie.Pruning
{
    public interface IPruningStrategy
    {
        bool ShouldPrune(in long currentMemory);
    }
}