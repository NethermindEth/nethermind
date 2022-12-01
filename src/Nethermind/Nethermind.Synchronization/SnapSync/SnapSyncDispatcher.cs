// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncDispatcher : SyncDispatcher<SnapSyncBatch>
    {
        public SnapSyncDispatcher(ISyncFeed<SnapSyncBatch>? syncFeed, ISyncPeerPool? syncPeerPool, IPeerAllocationStrategyFactory<SnapSyncBatch>? peerAllocationStrategy, ILogManager? logManager)
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
        }

        protected override async Task Dispatch(PeerInfo peerInfo, SnapSyncBatch batch, CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;

            //TODO: replace with a constant "snap"
            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>("snap", out var handler))
            {
                if (batch.AccountRangeRequest is not null)
                {
                    Task<AccountsAndProofs> task = handler.GetAccountRange(batch.AccountRangeRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }

                            SnapSyncBatch batchLocal = (SnapSyncBatch)state!;
                            if (t.IsCompletedSuccessfully)
                            {
                                batchLocal.AccountRangeResponse = t.Result;
                            }
                        }, batch);
                }
                else if (batch.StorageRangeRequest is not null)
                {
                    Task<SlotsAndProofs> task = handler.GetStorageRange(batch.StorageRangeRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }

                            SnapSyncBatch batchLocal = (SnapSyncBatch)state!;
                            if (t.IsCompletedSuccessfully)
                            {
                                batchLocal.StorageRangeResponse = t.Result;
                            }
                        }, batch);
                }
                else if (batch.CodesRequest is not null)
                {
                    Task<byte[][]> task = handler.GetByteCodes(batch.CodesRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }

                            SnapSyncBatch batchLocal = (SnapSyncBatch)state!;
                            if (t.IsCompletedSuccessfully)
                            {
                                batchLocal.CodesResponse = t.Result;
                            }
                        }, batch);
                }
                else if (batch.AccountsToRefreshRequest is not null)
                {
                    Task<byte[][]> task = handler.GetTrieNodes(batch.AccountsToRefreshRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }

                            SnapSyncBatch batchLocal = (SnapSyncBatch)state!;
                            if (t.IsCompletedSuccessfully)
                            {
                                batchLocal.AccountsToRefreshResponse = t.Result;
                            }
                        }, batch);
                }
            }

            await Task.CompletedTask;
        }
    }
}
