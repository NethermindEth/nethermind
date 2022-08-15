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

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.StateSync
{
    public class StateSyncDispatcher : SyncDispatcher<StateSyncBatch>
    {
        private string _trace;

        private readonly bool _snapSyncEnabled;

        public StateSyncDispatcher(ISyncFeed<StateSyncBatch> syncFeed, ISyncPeerPool syncPeerPool, IPeerAllocationStrategyFactory<StateSyncBatch> peerAllocationStrategy, ISyncConfig syncConfig, ILogManager logManager)
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
            _snapSyncEnabled = syncConfig.SnapSync;
        }

        protected override async Task Dispatch(PeerInfo peerInfo, StateSyncBatch batch, CancellationToken cancellationToken)
        {
            //string printByteArray(byte[] bytes)
            //{
            //    return string.Join(null, bytes.Select(b => b.ToString("X")));
            //}

            ISyncPeer peer = peerInfo.SyncPeer;

            if (_snapSyncEnabled)
            {
                if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>("snap", out var handler))
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

                            StateSyncBatch batchLocal = (StateSyncBatch)state!;
                            if (t.IsCompletedSuccessfully)
                            {
                                batchLocal.AccountsToRefreshResponse = t.Result;
                            }
                        }, batch);

                    return;
                }
            }

            //_trace += String.Join(Environment.NewLine, batch.RequestedNodes.Select(n => n.Level + "|" + n.NodeDataType + "|" + printByteArray(n.AccountPathNibbles) + "|" + printByteArray(n.PathNibbles)))
            //    + Environment.NewLine;

            Task<byte[][]> getNodeDataTask = peer.GetNodeData(batch.RequestedNodes.Select(n => n.Hash).ToArray(), cancellationToken);
            await getNodeDataTask.ContinueWith(
                (t, state) =>
                {
                    if (t.IsFaulted)
                    {
                        if (Logger.IsTrace) Logger.Error("DEBUG/ERROR Error after dispatching the state sync request", t.Exception);
                    }

                    StateSyncBatch batchLocal = (StateSyncBatch)state!;
                    if (t.IsCompletedSuccessfully)
                    {
                        batchLocal.Responses = t.Result;
                    }
                }, batch);
        }
    }
}
