// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;

namespace Nethermind.Synchronization.StateSync
{
    public class StateSyncDownloader : ISyncDownloader<StateSyncBatch>
    {
        private readonly ILogger Logger;

        public StateSyncDownloader(ILogManager logManager)
        {
            Logger = logManager.GetClassLogger();
        }

        public async Task Dispatch(PeerInfo peerInfo, StateSyncBatch batch, CancellationToken cancellationToken)
        {
            if (batch?.RequestedNodes is null || batch.RequestedNodes.Count == 0)
            {
                return;
            }

            ISyncPeer peer = peerInfo.SyncPeer;

            // Try NODEDATA protocol first if available
            if (peer.TryGetSatelliteProtocol(Protocol.NodeData, out INodeDataPeer nodeDataHandler))
            {
                if (await TryGetNodeDataViaNodeDataProtocol(nodeDataHandler, batch, cancellationToken, peer))
                {
                    return;
                }
            }

            // Try eth66 if protocol version is below eth67
            if (peer.ProtocolVersion < EthVersions.Eth67)
            {
                if (await TryGetNodeDataViaEthProtocol(peer, batch, cancellationToken))
                {
                    return;
                }
            }

            // Try SNAP protocol if available
            if (peer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer snapHandler))
            {
                if (await TryGetNodeDataViaSnapProtocol(snapHandler, batch, cancellationToken, peer))
                {
                    return;
                }
            }

            throw new InvalidOperationException("State sync dispatch was scheduled to a peer unable to serve state sync.");
        }

        private async Task<bool> TryGetNodeDataViaNodeDataProtocol(INodeDataPeer nodeDataHandler, StateSyncBatch batch, CancellationToken cancellationToken, ISyncPeer peer)
        {
            if (Logger.IsTrace) Logger.Trace($"Requested NodeData via NodeDataProtocol from peer {peer}");
            HashList? hashList = HashList.Rent(batch.RequestedNodes);
            try
            {
                batch.Responses = await nodeDataHandler.GetNodeData(hashList, cancellationToken);
                if (batch.Responses is not null && batch.Responses.Count > 0)
                {
                    return true;
                }
                else
                {
                    if (Logger.IsTrace) Logger.Trace($"Received empty response from NodeDataProtocol, trying next protocol for peer {peer}");
                    batch.Responses?.Dispose();
                    batch.Responses = null;
                    return false;
                }
            }
            catch (Exception e)
            {
                if (Logger.IsTrace) Logger.Error("DEBUG/ERROR Error after dispatching the NodeData request", e);
                return false;
            }
            finally
            {
                if (hashList is not null) HashList.Return(hashList);
            }
        }

        private async Task<bool> TryGetNodeDataViaEthProtocol(ISyncPeer peer, StateSyncBatch batch, CancellationToken cancellationToken)
        {
            if (Logger.IsTrace) Logger.Trace($"Requested NodeData via EthProtocol from peer {peer}");
            HashList? hashList = HashList.Rent(batch.RequestedNodes);
            try
            {
                batch.Responses = await peer.GetNodeData(hashList, cancellationToken);
                if (batch.Responses is not null && batch.Responses.Count > 0)
                {
                    return true;
                }
                else
                {
                    if (Logger.IsTrace) Logger.Trace($"Received empty response from EthProtocol, trying next protocol for peer {peer}");
                    batch.Responses?.Dispose();
                    batch.Responses = null;
                    return false;
                }
            }
            catch (Exception e)
            {
                if (Logger.IsTrace) Logger.Error("DEBUG/ERROR Error after dispatching the EthProtocol request", e);
                return false;
            }
            finally
            {
                if (hashList is not null) HashList.Return(hashList);
            }
        }

        private async Task<bool> TryGetNodeDataViaSnapProtocol(ISnapSyncPeer snapHandler, StateSyncBatch batch, CancellationToken cancellationToken, ISyncPeer peer)
        {
            GetTrieNodesRequest? getTrieNodesRequest = null;
            HashList? hashList = null;
            try
            {
                if (batch.NodeDataType == NodeDataType.Code)
                {
                    if (Logger.IsTrace) Logger.Trace($"Requested ByteCodes via SnapProtocol from peer {peer}");
                    hashList = HashList.Rent(batch.RequestedNodes);
                    batch.Responses = await snapHandler.GetByteCodes(new KeccakToValueKeccakList(hashList), cancellationToken);
                }
                else
                {
                    if (Logger.IsTrace) Logger.Trace($"Requested TrieNodes via SnapProtocol from peer {peer}");
                    getTrieNodesRequest = GetGroupedRequest(batch);
                    batch.Responses = await snapHandler.GetTrieNodes(getTrieNodesRequest, cancellationToken);
                }

                if (batch.Responses is not null && batch.Responses.Count > 0)
                {
                    return true;
                }
                else
                {
                    if (Logger.IsTrace) Logger.Trace($"Received empty response from SnapProtocol for peer {peer}");
                    batch.Responses?.Dispose();
                    batch.Responses = null;
                    return false;
                }
            }
            catch (Exception e)
            {
                if (Logger.IsTrace) Logger.Error("DEBUG/ERROR Error after dispatching the SnapProtocol request", e);
                return false;
            }
            finally
            {
                if (hashList is not null) HashList.Return(hashList);
                getTrieNodesRequest?.Dispose();
            }
        }

        /// <summary>
        /// SNAP protocol allows grouping of storage requests by account path.
        /// The grouping decrease requests size.
        /// </summary>
        private GetTrieNodesRequest GetGroupedRequest(StateSyncBatch batch)
        {
            GetTrieNodesRequest request = new() { RootHash = batch.StateRoot };

            Dictionary<Hash256AsKey?, List<(TreePath path, StateSyncItem syncItem)>> itemsGroupedByAccount = new();
            List<(TreePath path, StateSyncItem syncItem)> accountTreePaths = new();

            foreach (StateSyncItem? item in batch.RequestedNodes)
            {
                if (item.Address is not null)
                {
                    if (!itemsGroupedByAccount.TryGetValue(item.Address, out var storagePaths))
                    {
                        storagePaths = new List<(TreePath, StateSyncItem)>();
                        itemsGroupedByAccount[item.Address] = storagePaths;
                    }

                    storagePaths.Add((item.Path, item));
                }
                else
                {
                    accountTreePaths.Add((item.Path, item));
                }
            }

            ArrayPoolList<PathGroup> accountAndStoragePath = new ArrayPoolList<PathGroup>(
                accountTreePaths.Count + itemsGroupedByAccount.Count,
                accountTreePaths.Count + itemsGroupedByAccount.Count);
            request.AccountAndStoragePaths = accountAndStoragePath;

            int requestedNodeIndex = 0;
            int accountPathIndex = 0;
            for (; accountPathIndex < accountTreePaths.Count; accountPathIndex++)
            {
                (TreePath path, StateSyncItem syncItem) = accountTreePaths[accountPathIndex];
                accountAndStoragePath[accountPathIndex] = new PathGroup() { Group = new[] { Nibbles.EncodePath(path) } };

                // We validate the order of the response later and it has to be the same as RequestedNodes
                batch.RequestedNodes[requestedNodeIndex] = syncItem;

                requestedNodeIndex++;
            }

            foreach (var kvp in itemsGroupedByAccount)
            {
                byte[][] group = new byte[kvp.Value.Count + 1][];
                group[0] = kvp.Key?.Value.Bytes.ToArray();

                for (int groupIndex = 1; groupIndex < group.Length; groupIndex++)
                {
                    (TreePath path, StateSyncItem syncItem) = kvp.Value[groupIndex - 1];
                    group[groupIndex] = Nibbles.EncodePath(path);

                    // We validate the order of the response later and it has to be the same as RequestedNodes
                    batch.RequestedNodes[requestedNodeIndex] = syncItem;

                    requestedNodeIndex++;
                }

                accountAndStoragePath[accountPathIndex] = new PathGroup() { Group = group };

                accountPathIndex++;
            }

            if (batch.RequestedNodes.Count != requestedNodeIndex)
            {
                Logger.Warn($"INCORRECT number of paths RequestedNodes.Length:{batch.RequestedNodes.Count} <> requestedNodeIndex:{requestedNodeIndex}");
            }

            return request;
        }

        /// <summary>
        /// Present an array of StateSyncItem[] as IReadOnlyList<Keccak> to avoid allocating secondary array
        /// Also Rent and Return cache for single item to try and avoid allocating the HashList in common case
        /// </summary>
        private sealed class HashList : IReadOnlyList<Hash256>
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

            public Hash256 this[int index] => _items[index].Hash;

            public int Count => _items.Count;

            public IEnumerator<Hash256> GetEnumerator()
            {
                foreach (StateSyncItem item in _items)
                {
                    yield return item.Hash;
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// Transition class to prevent even larger change. Need to be removed later.
        /// </summary>
        private sealed class KeccakToValueKeccakList : IReadOnlyList<ValueHash256>
        {
            private readonly HashList _innerList;

            internal KeccakToValueKeccakList(HashList innerList)
            {
                _innerList = innerList;
            }

            public IEnumerator<ValueHash256> GetEnumerator()
            {
                foreach (Hash256 keccak in _innerList)
                {
                    yield return keccak;
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public int Count => _innerList.Count;

            public ValueHash256 this[int index] => _innerList[index];
        }
    }
}
