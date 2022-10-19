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
using Nethermind.Core.Extensions;
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
            Logger.Info("SNAP - Dispatched");
            //TODO: replace with a constant "snap"
            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>("snap", out var handler))
            {
                if (batch.AccountRangeRequest is not null)
                {
                    Logger.Info("SNAP - AccountRangeRequest Dispatched");
                    Task<AccountsAndProofs> task = handler.GetAccountRange(batch.AccountRangeRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                Logger.Info("SNAP - AccountRangeRequest Faulted");
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }

                            SnapSyncBatch batchLocal = (SnapSyncBatch)state!;
                            if (t.IsCompletedSuccessfully)
                            {
                                Logger.Info("SNAP - AccountRangeRequest Success");
                                batchLocal.AccountRangeResponse = t.Result;
                            }
                        }, batch);
                }
                else if (batch.StorageRangeRequest is not null)
                {
                    Logger.Info("SNAP - StorageRangeRequest Dispatched");
                    Task<SlotsAndProofs> task = handler.GetStorageRange(batch.StorageRangeRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                Logger.Info("SNAP - StorageRangeRequest Faulted");
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }

                            SnapSyncBatch batchLocal = (SnapSyncBatch)state!;
                            if (t.IsCompletedSuccessfully)
                            {
                                Logger.Info("SNAP - StorageRangeRequest Success");
                                batchLocal.StorageRangeResponse = t.Result;
                            }
                        }, batch);
                }
                else if (batch.CodesRequest is not null)
                {
                    Logger.Info("SNAP - CodesRequest Dispatched");
                    Task<byte[][]> task = handler.GetByteCodes(batch.CodesRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                Logger.Info("SNAP - CodesRequest Faulted");
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }

                            SnapSyncBatch batchLocal = (SnapSyncBatch)state!;
                            if (t.IsCompletedSuccessfully)
                            {
                                Logger.Info("SNAP - CodesRequest Success");
                                batchLocal.CodesResponse = t.Result;
                            }
                        }, batch);
                }
                else if (batch.AccountsToRefreshRequest is not null)
                {
                    Logger.Info("SNAP - AccountsToRefreshRequest Dispatched");
                    Task<byte[][]> task = handler.GetTrieNodes(batch.AccountsToRefreshRequest, cancellationToken);

                    await task.ContinueWith(
                        (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                Logger.Info("SNAP - AccountsToRefreshRequest Faulted");
                                if (Logger.IsTrace)
                                    Logger.Error("DEBUG/ERROR Error after dispatching the snap sync request", t.Exception);
                            }

                            SnapSyncBatch batchLocal = (SnapSyncBatch)state!;
                            if (t.IsCompletedSuccessfully)
                            {
                                Logger.Info("SNAP - AccountsToRefreshRequest Success");
                                batchLocal.AccountsToRefreshResponse = t.Result;
                            }
                        }, batch);
                }
            }

            await Task.CompletedTask;
        }
    }
}
