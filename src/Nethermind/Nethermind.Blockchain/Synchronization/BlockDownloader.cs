using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Logging;

namespace Nethermind.Blockchain.Synchronization
{
    public interface IBlockDownloader
    {
        Task<Block[]> DownloadBlocks();
    }
    
    public class BlockDownloader : IBlockDownloader
    {
        private int _currentBatchSize = 256;
        
        public const int MinBatchSize = 8;
        
        public const int MaxBatchSize = 512;
        
        private void IncreaseBatchSize() => _currentBatchSize = Math.Min(MaxBatchSize, _currentBatchSize * 2);

        private void DecreaseBatchSize() => _currentBatchSize = Math.Max(MinBatchSize, _currentBatchSize / 2);
        
        private readonly IEthSyncPeerSelector _peerSelector;
        private readonly ILogger _logger;

        public BlockDownloader(IEthSyncPeerSelector peerSelector, ILogManager logManager)
        {
            _peerSelector = peerSelector;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public Task<Block[]> DownloadBlocks()
        {
            return Task.FromResult((Block[])null);
        }
    }
}