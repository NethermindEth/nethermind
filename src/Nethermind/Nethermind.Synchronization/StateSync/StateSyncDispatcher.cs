// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
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
            if (batch?.RequestedNodes is null || batch.RequestedNodes.Count == 0)
            {
                return;
            }

            ISyncPeer peer = peerInfo.SyncPeer;
            Task<byte[][]> task = null;
            HashList? hashList = null;
            if (peer.TryGetSatelliteProtocol("snap", out ISnapSyncPeer handler))
            {
                if (batch.NodeDataType == NodeDataType.Code)
                {
                    hashList = HashList.Rent(batch.RequestedNodes);
                    task = handler.GetByteCodes(hashList, cancellationToken);
                }
                else
                {
                    GetTrieNodesRequest request = GetGroupedRequest(batch);
                    task = handler.GetTrieNodes(request, cancellationToken);
                }
            }

            if (task is null)
            {
                throw new InvalidOperationException("State sync dispatch was scheduled to a peer unable to serve state sync.");
            }

            try
            {
                batch.Responses = await task;

                if (hashList is not null) HashList.Return(hashList);
            }
            catch (Exception e)
            {
                if (Logger.IsTrace) Logger.Error("DEBUG/ERROR Error after dispatching the state sync request", e);
            }
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

            if (batch.RequestedNodes.Count != requestedNodeIndex)
            {
                Logger.Warn($"INCORRECT number of paths RequestedNodes.Length:{batch.RequestedNodes.Count} <> requestedNodeIndex:{requestedNodeIndex}");
            }

            return request;
        }

        private static byte[] EncodePath(byte[] input) => input.Length == 64 ? Nibbles.ToBytes(input) : Nibbles.ToCompactHexEncoding(input);

        /// <summary>
        /// Present an array of StateSyncItem[] as IReadOnlyList<Keccak> to avoid allocating secondary array
        /// Also Rent and Return cache for single item to try and avoid allocating the HashList in common case
        /// </summary>
        private sealed class HashList : IReadOnlyList<Keccak>
        {
            private static HashList s_cache;

            private IList<StateSyncItem> _items;

            public static HashList Rent(IList<StateSyncItem> items)
            {
                HashList hashList = Interlocked.Exchange(ref s_cache, null) ?? new HashList();
                hashList.Initialize(items);
                return hashList;
            }

            public static void Return(HashList hashList)
            {
                hashList.Reset();
                Volatile.Write(ref s_cache, hashList);
            }

            public void Initialize(IList<StateSyncItem> items)
            {
                _items = items;
            }

            public void Reset()
            {
                _items = null;
            }

            public Keccak this[int index] => _items[index].Hash;

            public int Count => _items.Count;

            public IEnumerator<Keccak> GetEnumerator()
            {
                foreach (StateSyncItem item in _items)
                {
                    yield return item.Hash;
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
