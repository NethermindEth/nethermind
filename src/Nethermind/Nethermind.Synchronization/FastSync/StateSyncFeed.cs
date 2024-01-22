// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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
        private readonly TreeSync _treeSync;
        private bool _disposed = false;
        private SyncMode _currentSyncMode = SyncMode.None;

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.State;

        public StateSyncFeed(
            TreeSync treeSync,
            ILogManager logManager)
        {
            _treeSync = treeSync ?? throw new ArgumentNullException(nameof(treeSync));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public override async Task<StateSyncBatch?> PrepareRequest(CancellationToken token = default)
        {
            try
            {
                (bool continueProcessing, bool finishSyncRound) = _treeSync.ValidatePrepareRequest(_currentSyncMode);

                if (finishSyncRound)
                {
                    FinishThisSyncRound();
                }

                if (!continueProcessing)
                {
                    return EmptyBatch!;
                }

                return await _treeSync.PrepareRequest(_currentSyncMode);
            }
            catch (Exception e)
            {
                _logger.Error("Error when preparing a batch", e);
                return await Task.FromResult(EmptyBatch);
            }
        }

        public override SyncResponseHandlingResult HandleResponse(StateSyncBatch? batch, PeerInfo? peer = null)
        {
            return _treeSync.HandleResponse(batch, peer);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        public override void SyncModeSelectorOnChanged(SyncMode current)
        {
            if (_disposed) return;
            if (CurrentState == SyncFeedState.Dormant)
            {
                if ((current & SyncMode.StateNodes) == SyncMode.StateNodes)
                {
                    _treeSync.ResetStateRootToBestSuggested(CurrentState);
                    Activate();
                }
            }

            _currentSyncMode = current;
        }

        private void FinishThisSyncRound()
        {
            lock (_handleWatch)
            {
                FallAsleep();
                _treeSync.ResetStateRoot(CurrentState);
            }
        }

        public override bool IsFinished => false; // Check MultiSyncModeSelector
    }
}
