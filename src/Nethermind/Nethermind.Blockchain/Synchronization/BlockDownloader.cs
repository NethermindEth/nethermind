/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Logging;

namespace Nethermind.Blockchain.Synchronization
{
    public class BlockDownloader : IBlockDownloader
    {
        private int _currentBatchSize = 256;
        
        public const int MinBatchSize = 8;
        
        public const int MaxBatchSize = 512;
        
        private void IncreaseBatchSize() => _currentBatchSize = Math.Min(MaxBatchSize, _currentBatchSize * 2);

        private void DecreaseBatchSize() => _currentBatchSize = Math.Max(MinBatchSize, _currentBatchSize / 2);
        
        private readonly IEthSyncPeerPool _peerPool;
        private readonly ILogger _logger;

        public BlockDownloader(IEthSyncPeerPool peerPool, ILogManager logManager)
        {
            _peerPool = peerPool;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public Task<Block[]> DownloadBlocks()
        {
            return Task.FromResult((Block[])null);
        }
    }
}