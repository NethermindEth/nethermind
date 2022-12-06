// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
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
        public StateSyncDispatcher(ISyncFeed<StateSyncBatch> syncFeed, ISyncPeerPool syncPeerPool, IPeerAllocationStrategyFactory<StateSyncBatch> peerAllocationStrategy, ILogManager logManager)
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
        }

        protected override async Task Dispatch(PeerInfo peerInfo, StateSyncBatch batch, CancellationToken cancellationToken)
        {
            if (batch?.RequestedNodes is null || batch.RequestedNodes.Length == 0)
            {
                return;
            }

            ISyncPeer peer = peerInfo.SyncPeer;
            Keccak[]? a = batch.RequestedNodes.Select(n => n.Hash).ToArray();
            Task<byte[][]> task = null;

            // Use GETNODEDATA if possible
            if (peer.Node.EthDetails.Equals("eth66"))
            {
                task = peer.GetNodeData(a, cancellationToken);
            }
            // GETNODEDATA is not supported so we try with SNAP protocol
            else if (peer.TryGetSatelliteProtocol("snap", out ISnapSyncPeer handler))
            {
                if (batch.NodeDataType == NodeDataType.Code)
                {
                    task = handler.GetByteCodes(a, cancellationToken);
                }
                else
                {
                    GetTrieNodesRequest request = GetGroupedRequest(batch);
                    task = handler.GetTrieNodes(request, cancellationToken);
                }
            }

            if (task is null)
            {
                return;
            }

            await task.ContinueWith(
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

        /// <summary>
        /// SNAP protocol allows grouping of storage requests by account path.
        /// The grouping decrease requests size.
        /// </summary>
        private GetTrieNodesRequest GetGroupedRequest(StateSyncBatch batch)
        {
            GetTrieNodesRequest request = new() { RootHash = batch.StateRoot };

            Dictionary<byte[], List<(byte[] path, StateSyncItem syncItem)>> itemsGroupedByAccount = new(Bytes.EqualityComparer);
            List<(byte[] path, StateSyncItem syncItem)> accountTreePaths = new();

            foreach (StateSyncItem? item in batch.RequestedNodes)
            {
                if (item.AccountPathNibbles?.Length > 0)
                {
                    if (!itemsGroupedByAccount.TryGetValue(item.AccountPathNibbles, out var storagePaths))
                    {
                        storagePaths = new List<(byte[], StateSyncItem)>();
                        itemsGroupedByAccount[item.AccountPathNibbles] = storagePaths;
                    }

                    storagePaths.Add((item.PathNibbles, item));
                }
                else
                {
                    accountTreePaths.Add((item.PathNibbles, item));
                }
            }

            request.AccountAndStoragePaths = new PathGroup[accountTreePaths.Count + itemsGroupedByAccount.Count];

            int requestedNodeIndex = 0;
            int accountPathIndex = 0;
            for (; accountPathIndex < accountTreePaths.Count; accountPathIndex++)
            {
                (byte[] path, StateSyncItem syncItem) accountPath = accountTreePaths[accountPathIndex];
                request.AccountAndStoragePaths[accountPathIndex] = new PathGroup() { Group = new[] { EncodePath(accountPath.path) } };

                // We validate the order of the response later and it has to be the same as RequestedNodes
                batch.RequestedNodes[requestedNodeIndex] = accountPath.syncItem;

                requestedNodeIndex++;
            }

            foreach (var kvp in itemsGroupedByAccount)
            {
                byte[][] group = new byte[kvp.Value.Count + 1][];
                group[0] = EncodePath(kvp.Key);

                for (int groupIndex = 1; groupIndex < group.Length; groupIndex++)
                {
                    (byte[] path, StateSyncItem syncItem) storagePath = kvp.Value[groupIndex - 1];
                    group[groupIndex] = EncodePath(storagePath.path);

                    // We validate the order of the response later and it has to be the same as RequestedNodes
                    batch.RequestedNodes[requestedNodeIndex] = storagePath.syncItem;

                    requestedNodeIndex++;
                }

                request.AccountAndStoragePaths[accountPathIndex] = new PathGroup() { Group = group };

                accountPathIndex++;
            }

            if (batch.RequestedNodes.Length != requestedNodeIndex)
            {
                Logger.Warn($"INCORRECT number of paths RequestedNodes.Length:{batch.RequestedNodes.Length} <> requestedNodeIndex:{requestedNodeIndex}");
            }

            return request;
        }

        private static byte[] EncodePath(byte[] input) => input.Length == 64 ? Nibbles.ToBytes(input) : Nibbles.ToCompactHexEncoding(input);
    }
}
