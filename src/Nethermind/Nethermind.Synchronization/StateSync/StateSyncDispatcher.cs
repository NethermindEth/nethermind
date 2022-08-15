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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;

namespace Nethermind.Synchronization.StateSync
{
    public class StateSyncDispatcher : SyncDispatcher<StateSyncBatch>
    {
        //private string _trace;

        private readonly bool _snapSyncEnabled;

        public StateSyncDispatcher(ISyncFeed<StateSyncBatch> syncFeed, ISyncPeerPool syncPeerPool, IPeerAllocationStrategyFactory<StateSyncBatch> peerAllocationStrategy, ISyncConfig syncConfig, ILogManager logManager)
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
            _snapSyncEnabled = syncConfig.SnapSync;
        }

        protected override async Task Dispatch(PeerInfo peerInfo, StateSyncBatch batch, CancellationToken cancellationToken)
        {
            if (batch == null || batch.RequestedNodes == null || batch.RequestedNodes.Length == 0)
            {
                return;
            }

            //string printByteArray(byte[] bytes)
            //{
            //    return string.Join(null, bytes.Select(b => b.ToString("X")));
            //}

            ISyncPeer peer = peerInfo.SyncPeer;

            if (_snapSyncEnabled)
            {
                GetTrieNodesRequest request = GetRequest(batch);

                if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>("snap", out var handler))
                {
                    Task<byte[][]> task = handler.GetTrieNodes(request, cancellationToken);
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
                                batchLocal.Responses = t.Result;
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

        private GetTrieNodesRequest GetRequest(StateSyncBatch batch)
        {
            GetTrieNodesRequest request = new();
            request.RootHash = batch.StateRoot;

            Dictionary<byte[], List<byte[]>> dict = new(Bytes.EqualityComparer);
            List<byte[]> accountTreePaths = new();

            foreach (var item in batch.RequestedNodes)
            {
                if (item.AccountPathNibbles == null || item.AccountPathNibbles.Length == 0)
                {
                    accountTreePaths.Add(item.PathNibbles);
                }
                else
                {
                    if (!dict.TryGetValue(item.AccountPathNibbles, out var storagePaths))
                    {
                        storagePaths = new List<byte[]>();
                        dict[item.AccountPathNibbles] = storagePaths;
                    }

                    storagePaths.Add(item.PathNibbles);
                }
            }

            request.AccountAndStoragePaths = new PathGroup[accountTreePaths.Count + dict.Count];

            int i = 0;
            for (; i < accountTreePaths.Count; i++)
            {
                request.AccountAndStoragePaths[i] = new PathGroup() { Group = new[] { EncodePath(accountTreePaths[i]) } };
            }

            foreach (var kvp in dict)
            {
                byte[][] group = new byte[kvp.Value.Count + 1][];
                group[0] = EncodePath(kvp.Key);

                for (int groupIndex = 1; groupIndex < group.Length; groupIndex++)
                {
                    group[groupIndex] = EncodePath(kvp.Value[groupIndex - 1]);
                }

                request.AccountAndStoragePaths[i] = new PathGroup() { Group = group };
                i++;
            }

            return request;
        }

        private byte[] EncodePath(byte[] input)
        {
            if (input.Length == 64)
            {
                // TODO: Convert 64 nibbles into 32 bytes
                
            }
            else
            {
                // TODO: Add compact encoding
            }

            return input;
        }
    }
}
