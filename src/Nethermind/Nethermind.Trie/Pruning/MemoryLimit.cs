using System.Diagnostics;

namespace Nethermind.Trie.Pruning
{
    [DebuggerDisplay("{_memoryLimit/(1024*1024)}MB")]
    public class MemoryLimit : IPruningStrategy
    {
        private readonly long _memoryLimit;

        public MemoryLimit(long memoryLimit)
        {
            _memoryLimit = memoryLimit;
        }

        public bool ShouldPrune(in long blockNumber, in long highestBlockNumber, in long currentMemory)
        {
            return currentMemory >= _memoryLimit;
        }
    }
}