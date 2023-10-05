// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.FastSync
{
    public partial class StateSyncFeed : SyncFeed<StateSyncBatch?>, IDisposable
    {
        private const StateSyncBatch EmptyBatch = null;

        private readonly Stopwatch _handleWatch = new();
        private readonly ILogger _logger;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly TreeSync _treeSync;

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.State;

        public StateSyncFeed(
            ISyncModeSelector syncModeSelector,
            TreeSync treeSync,
            ILogManager logManager)
        {
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _treeSync = treeSync ?? throw new ArgumentNullException(nameof(treeSync));
            _syncModeSelector.Changed += SyncModeSelectorOnChanged;

            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public override async Task<StateSyncBatch?> PrepareRequest(CancellationToken token = default)
        {
            try
            {
                (bool continueProcessing, bool finishSyncRound) = _treeSync.ValidatePrepareRequest(_syncModeSelector.Current);

                if (finishSyncRound)
                {
                    FinishThisSyncRound();
                }

                if (!continueProcessing)
                {
                    return EmptyBatch!;
                }

                return await _treeSync.PrepareRequest(_syncModeSelector.Current);
            }
            catch (Exception e)
            {
                _logger.Error("Error when preparing a batch", e);
                return await Task.FromResult(EmptyBatch);
            }
        }

        public override SyncResponseHandlingResult HandleResponse(StateSyncBatch? batch, PeerInfo peer = null)
        {
            return _treeSync.HandleResponse(batch, peer);
        }

        public void Dispose()
        {
            _syncModeSelector.Changed -= SyncModeSelectorOnChanged;
        }

        private void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (CurrentState == SyncFeedState.Dormant)
            {
                if ((e.Current & SyncMode.StateNodes) == SyncMode.StateNodes)
                {
                    _treeSync.ResetStateRootToBestSuggested(CurrentState);
                    Activate();
                }
            }
        }

        private void FinishThisSyncRound()
        {
            lock (_handleWatch)
            {
                FallAsleep();
                _treeSync.ResetStateRoot(CurrentState);
            }
        }
    }
}
