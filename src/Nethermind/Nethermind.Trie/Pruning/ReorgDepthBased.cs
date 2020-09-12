using System.Diagnostics;

namespace Nethermind.Trie.Pruning
{
    [DebuggerDisplay("{_depth}")]
    public class ReorgDepthBased : IPruningStrategy
    {
        private readonly long _depth;

        public ReorgDepthBased(long depth)
        {
            _depth = depth;
        }

        public bool ShouldPrune(in long blockNumber, in long highestBlockNumber, in long currentMemory)
        {
            return highestBlockNumber - blockNumber >= _depth;
        }
    }
}