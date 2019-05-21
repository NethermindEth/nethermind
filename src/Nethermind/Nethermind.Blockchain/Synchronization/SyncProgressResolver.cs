using System;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization
{
    internal class SyncProgressResolver : ISyncProgressResolver
    {
        private const int _maxLookup = 64;
        
        private readonly IBlockTree _blockTree;
        private readonly INodeDataDownloader _nodeDataDownloader;
        private ILogger _logger;
        
        public SyncProgressResolver(IBlockTree blockTree, INodeDataDownloader nodeDataDownloader, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _nodeDataDownloader = nodeDataDownloader ?? throw new ArgumentNullException(nameof(nodeDataDownloader));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public long FindBestFullState()
        {
            /* There is an interesting scenario (unlikely) here where we download more than 'full sync threshold'
             blocks in full sync but they are not processed immediately so we switch to node sync
             and the blocks that we downloaded are processed from their respective roots
             and the next full sync will be after a leap.
             This scenario is still correct. It may be worth to analyze what happens
             when it causes a full sync vs node sync race at every block.*/

            BlockHeader bestSuggested = _blockTree.BestSuggested;
            BlockHeader head = _blockTree.Head;
            long bestFullState = head?.Number ?? 0;
            long maxLookup = Math.Min(_maxLookup * 2, bestSuggested?.Number ?? 0L - bestFullState);

            for (int i = 0; i < maxLookup; i++)
            {
                if (bestSuggested == null)
                {
                    break;
                }

                if (_nodeDataDownloader.IsFullySynced(bestSuggested))
                {
                    bestFullState = bestSuggested.Number;
                    break;
                }

                bestSuggested = _blockTree.FindHeader(bestSuggested.ParentHash);
            }

            return bestFullState;
        }

        public long FindBestHeader()
        {
            return _blockTree.BestSuggested?.Number ?? 0;
        }

        public long FindBestFullBlock()
        {
            /* avoiding any potential concurrency issue */
            return Math.Min(FindBestHeader(), _blockTree.BestSuggestedFullBlock?.Number ?? 0);
        }
    }
}