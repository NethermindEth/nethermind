namespace Nethermind.Trie.Pruning
{
    public class DepthAndMemoryBased : IPruningStrategy
    {
        private IPruningStrategy _memoryLimit;
        private IPruningStrategy _reorgDepthLimit;
        
        public DepthAndMemoryBased(long reorgDepth, long memoryLimit)
        {
            _memoryLimit = new MemoryLimit(memoryLimit);
            _reorgDepthLimit = new ReorgDepthBased(reorgDepth);
        }
        
        public bool ShouldPrune(in long blockNumber, in long highestBlockNumber, in long currentMemory)
        {
            bool memoryLimitReached = _memoryLimit.ShouldPrune(blockNumber, highestBlockNumber, currentMemory);
            bool reorgDepthReached = _reorgDepthLimit.ShouldPrune(blockNumber, highestBlockNumber, currentMemory);
            return memoryLimitReached || reorgDepthReached;
        }
    }
}