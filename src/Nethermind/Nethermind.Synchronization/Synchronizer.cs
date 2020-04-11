//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.TotalSync;

namespace Nethermind.Synchronization
{
    public class Synchronizer : ISynchronizer
    {
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly ILogManager _logManager;
        private readonly ISyncReport _syncReport;
        
        private CancellationTokenSource _syncCancellation = new CancellationTokenSource();

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs> SyncEvent;

        private readonly IDbProvider _dbProvider;
        private ISyncModeSelector _syncMode;

        public Synchronizer(
            IDbProvider dbProvider,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncPeerPool peerPool,
            INodeStatsManager nodeStatsManager,
            ISyncModeSelector syncModeSelector,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _syncMode = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _logManager = logManager;

            _syncReport = new SyncReport(_syncPeerPool, _nodeStatsManager, _syncMode, syncConfig, logManager);
        }

        public void Start()
        {
            StartFullSyncComponents();
            if (_syncConfig.FastSync)
            {
                if (_syncConfig.FastBlocks)
                {
                    StartFastBlocksComponents();
                }

                StartFastSyncComponents();
                StartStateSyncComponents();
            }
        }

        public async Task StopAsync()
        {
            await Task.CompletedTask;
        }

        private void StartFullSyncComponents()
        {
            FullSyncFeed fullSyncFeed = new FullSyncFeed(_syncMode);
            BlockDownloader fullSyncBlockDownloader = new BlockDownloader(fullSyncFeed, _syncPeerPool, _blockTree, _blockValidator, _sealValidator, _syncReport, _receiptStorage, _specProvider, _logManager);
            fullSyncBlockDownloader.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Full sync block downloader failed", t.Exception);
                }
            });
        }
        
        private void StartStateSyncComponents()
        {
            StateSyncFeed stateSyncFeed = new StateSyncFeed(_dbProvider.CodeDb, _dbProvider.StateDb, new MemDb(), _syncMode, _blockTree, _logManager);
            StateSyncDispatcher stateSyncDispatcher = new StateSyncDispatcher(stateSyncFeed, _syncPeerPool, new StateSyncAllocationStrategyFactory(), _logManager);
            Task syncDispatcherTask = stateSyncDispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("State sync failed", t.Exception);
                }
            });
        }

        private void StartFastBlocksComponents()
        {
            FastBlocksPeerAllocationStrategyFactory fastFactory = new FastBlocksPeerAllocationStrategyFactory();

            FastHeadersSyncFeed headersFeed = new FastHeadersSyncFeed(_blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            HeadersSyncDispatcher headersDispatcher = new HeadersSyncDispatcher(headersFeed, _syncPeerPool, fastFactory, _logManager);
            Task headersTask = headersDispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Fast blocks headers downloader failed", t.Exception);
                }
            });

            headersFeed.Activate();

            FastBodiesSyncFeed bodiesFeed = new FastBodiesSyncFeed(_blockTree, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            BodiesSyncDispatcher bodiesDispatcher = new BodiesSyncDispatcher(bodiesFeed, _syncPeerPool, fastFactory, _logManager);
            Task bodiesTask = bodiesDispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Fast bodies sync failed", t.Exception);
                }
            });

            bodiesFeed.Activate();

            FastReceiptsSyncFeed receiptsFeed = new FastReceiptsSyncFeed(_specProvider, _blockTree, _receiptStorage, _syncPeerPool, _syncConfig, _syncReport, _logManager);
            ReceiptsSyncDispatcher receiptsDispatcher = new ReceiptsSyncDispatcher(receiptsFeed, _syncPeerPool, fastFactory, _logManager);
            Task receiptsTask = receiptsDispatcher.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Fast receipts sync failed", t.Exception);
                }
            });

            receiptsFeed.Activate();
        }

        private void StartFastSyncComponents()
        {
            FastSyncFeed fastSyncFeed = new FastSyncFeed(_syncMode, _syncConfig);
            BlockDownloader downloader = new BlockDownloader(fastSyncFeed, _syncPeerPool, _blockTree, _blockValidator, _sealValidator, _syncReport, _receiptStorage, _specProvider, _logManager);
            downloader.Start(_syncCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("Fast sync failed", t.Exception);
                }
            });
        }

        public void Dispose()
        {
            _syncCancellation?.Dispose();
            _syncReport?.Dispose();
        }
    }
}