//  Copyright (c) 2021 Demerzel Solutions Limited
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
//

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Prometheus;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncDispatcher : SyncDispatcher<SnapSyncBatch>
    {
        public static Counter SnapSyncRequest = Prometheus.Metrics.CreateCounter("snap_sync_request", "Snap sync request", "request_type", "status");

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
                    Logger.Info($"Using {peerInfo} to send account range request {batch.AccountRangeRequest.BlockNumber}");
                    Task<AccountsAndProofs> task = handler.GetAccountRange(batch.AccountRangeRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                SnapSyncRequest.WithLabels("account_range", "fail").Inc();
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }
                            else
                            {
                                SnapSyncRequest.WithLabels("account_range", "success").Inc();
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
                    Logger.Info($"Using {peerInfo} to send storage range request of length {batch.StorageRangeRequest.Accounts.Length}");
                    Task<SlotsAndProofs> task = handler.GetStorageRange(batch.StorageRangeRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                SnapSyncRequest.WithLabels("storage_range", "fail").Inc();
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }
                            else
                            {
                                SnapSyncRequest.WithLabels("storage_range", "success").Inc();
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
                    Logger.Info($"Using {peerInfo} to send code request {batch.CodesRequest.Length}");
                    Task<byte[][]> task = handler.GetByteCodes(batch.CodesRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                SnapSyncRequest.WithLabels("byte_codes", "fail").Inc();
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }
                            else
                            {
                                SnapSyncRequest.WithLabels("byte_codes", "success").Inc();
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
                    Logger.Info($"Using {peerInfo} to send account to refresh {batch.AccountsToRefreshRequest}");
                    Task<byte[][]> task = handler.GetTrieNodes(batch.AccountsToRefreshRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                SnapSyncRequest.WithLabels("account_refresh", "fail").Inc();
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }
                            else
                            {
                                SnapSyncRequest.WithLabels("account_refresh", "success").Inc();
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
