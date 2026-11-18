// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncDownloader(ILogManager? logManager) : ISyncDownloader<SnapSyncBatch>
    {
        private readonly ILogger Logger = logManager.GetClassLogger<SnapSyncDownloader>();
        private ISnapProvider? _provider;
        private SnapRecoveryCoordinator? _recovery;

        internal SnapSyncDownloader(ILogManager logManager, ISnapProvider provider, SnapRecoveryCoordinator recovery) : this(logManager)
            => (_provider, _recovery) = (provider, recovery);

        private TimeProvider ClockFor(SnapSyncBatch batch) => (batch.RecoveryContext ?? _recovery)?.TimeProvider ?? TimeProvider.System;

        public async Task Dispatch(PeerInfo peerInfo, SnapSyncBatch batch, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;

            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer handler))
            {
                ISnapProvider? snapProvider = batch.ProviderContext ?? _provider;
                SnapRecoveryCoordinator? recovery = batch.RecoveryContext ?? _recovery;
                if (batch.StorageRangeRequest is { } storage && snapProvider is SnapProvider provider)
                    batch.DispatchEvidence = provider.CaptureStorageEvidence(storage);
                bool rooted = batch.AccountRangeRequest is not null || batch.StorageRangeRequest is not null || batch.AccountsToRefreshRequest is not null;
                bool unknown = batch.StorageRangeRequest is not null &&
                    (batch.DispatchEvidence?.IsUnknown ?? true);
                if (rooted && recovery is not null)
                {
                    batch.RecoveryLease = recovery.TryReserve(peerInfo, unknown, batch.StorageRangeRequest?.Accounts.Count ?? 0, batch.RecoveryCohort);
                    if (batch.RecoveryLease is null)
                    {
                        batch.LocalDeferred = true;
                        return;
                    }
                }
                // Timed on the clock that drives snap recovery, so a response's speed is measured on the same time
                // base as its readiness and pivot windows rather than on an unrelated wall clock.
                // Resolved again in finally rather than held in a local, so the clock is not hoisted into the async state machine.
                long started = ClockFor(batch).GetTimestamp();
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (batch.RecoveryLease?.Sent() == false) { batch.LocalDeferred = true; return; }
                    if (batch.AccountRangeRequest is not null)
                    {
                        batch.AccountRangeResponse = await handler.GetAccountRange(batch.AccountRangeRequest, cancellationToken);
                    }
                    else if (batch.StorageRangeRequest is not null)
                    {
                        batch.StorageRangeResponse = await handler.GetStorageRange(batch.StorageRangeRequest, cancellationToken);
                    }
                    else if (batch.CodesRequest is not null)
                    {
                        batch.CodesResponse = await handler.GetByteCodes(batch.CodesRequest, cancellationToken);
                    }
                    else if (batch.AccountsToRefreshRequest is { Paths.Count: > 0 })
                    {
                        // Refresh a single account via GetAccountRange so its storage root is verified against
                        // the state root. Use limit = path + 1 to avoid start == limit, which some peers treat as
                        // an empty range. (IncrementPath is a no-op only for the unreachable MaxValue path.)
                        AccountsToRefreshRequest request = batch.AccountsToRefreshRequest;
                        AccountWithStorageStartingHash account = request.Paths[0];
                        PathWithAccount pathAndAccount = account.PathAndAccount
                            ?? throw new InvalidOperationException("An account refresh request requires an account path.");
                        ValueHash256 path = pathAndAccount.Path;
                        AccountRange range = new(request.RootHash, path, path.IncrementPath());
                        batch.AccountsToRefreshResponse = await handler.GetAccountRange(range, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (Logger.IsDebug) Logger.Debug($"Snap sync request cancelled. Request: {batch}");
                }
                catch (Exception e)
                {
                    Logger.DebugError($"Error after dispatching the snap sync request. Request: {batch}", e);
                }
                finally
                {
                    batch.ResponseMilliseconds = (long)ClockFor(batch).GetElapsedTime(started).TotalMilliseconds;
                }
            }
        }
    }
}
