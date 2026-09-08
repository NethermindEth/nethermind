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
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;

namespace Nethermind.Synchronization.StateSync
{
    public class StateSyncDownloader(ILogManager logManager) : ISyncDownloader<StateSyncBatch>
    {
        private readonly ILogger Logger = logManager.GetClassLogger<StateSyncDownloader>();

        public async Task Dispatch(PeerInfo peerInfo, StateSyncBatch batch, CancellationToken cancellationToken)
        {
            if (batch?.RequestedNodes is null || batch.RequestedNodes.Count == 0)
            {
                return;
            }

            ISyncPeer peer = peerInfo.SyncPeer;
            bool anyProtocolAttempted = false;

            // Snap is tried first as it is the protocol most peers still serve state from. A peer that
            // advertises a protocol but answers with an empty response is not necessarily unable to serve
            // state - it may simply be out of sync - so each protocol falls through to the next one.
            if (peer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer snapHandler)
                && (batch.NodeDataType == NodeDataType.Code || ProtocolSupportsTrieNodes(snapHandler)))
            {
                anyProtocolAttempted = true;
                if (await TryDispatchViaSnap(peer, snapHandler, batch, cancellationToken)) return;
            }

            if (ProtocolSupportsNodeData(peer))
            {
                anyProtocolAttempted = true;
                if (await TryDispatchViaNodeData(peer, batch, cancellationToken)) return;
            }

            if (!anyProtocolAttempted)
            {
                throw new InvalidOperationException("State sync dispatch was scheduled to a peer unable to serve state sync.");
            }

            if (Logger.IsDebug) Logger.Debug($"All protocols returned an empty response for peer {peer}. The peer may be out of sync.");
        }

        private async Task<bool> TryDispatchViaSnap(ISyncPeer peer, ISnapSyncPeer snapHandler, StateSyncBatch batch, CancellationToken cancellationToken)
        {
            HashList? hashList = null;
            GetTrieNodesRequest? getTrieNodesRequest = null;
            try
            {
                Task<IByteArrayList> task;
                if (batch.NodeDataType == NodeDataType.Code)
                {
                    if (Logger.IsTrace) Logger.Trace($"Requested ByteCodes via SnapProtocol from peer {peer}");
                    hashList = HashList.Rent(batch.RequestedNodes);
                    task = snapHandler.GetByteCodes(new KeccakToValueKeccakList(hashList), cancellationToken);
                }
                else
                {
                    if (Logger.IsTrace) Logger.Trace($"Requested TrieNodes via SnapProtocol from peer {peer}");
                    getTrieNodesRequest = GetGroupedRequest(batch);
                    task = snapHandler.GetTrieNodes(getTrieNodesRequest, cancellationToken);
                }

                return TryKeepResponses(batch, await task);
            }
            catch (Exception e)
            {
                Logger.TraceError("Error after dispatching the state sync request over the Snap protocol", e);
                return false;
            }
            finally
            {
                if (hashList is not null) HashList.Return(hashList);
                getTrieNodesRequest?.Dispose();
            }
        }

        private async Task<bool> TryDispatchViaNodeData(ISyncPeer peer, StateSyncBatch batch, CancellationToken cancellationToken)
        {
            if (Logger.IsTrace) Logger.Trace($"Requested NodeData via EthProtocol from peer {peer}");
            HashList hashList = HashList.Rent(batch.RequestedNodes);
            try
            {
                return TryKeepResponses(batch, await peer.GetNodeData(hashList, cancellationToken));
            }
            catch (Exception e)
            {
                Logger.TraceError("Error after dispatching the state sync request over the Eth protocol", e);
                return false;
            }
            finally
            {
                HashList.Return(hashList);
            }
        }

        /// <returns><see langword="true"/> if the response carried any node, in which case it is kept on the batch.</returns>
        private static bool TryKeepResponses(StateSyncBatch batch, IByteArrayList? responses)
        {
            if (responses is null || responses.Count == 0)
            {
                responses?.Dispose();
                return false;
            }

            batch.Responses = responses;
            return true;
        }

        protected virtual bool ProtocolSupportsNodeData(ISyncPeer peer) => peer.ProtocolVersion < EthVersions.Eth67;

        protected virtual bool ProtocolSupportsTrieNodes(ISnapSyncPeer peer) => peer.CanGetTrieNodes();

        /// <summary>
        /// SNAP protocol allows grouping of storage requests by account path.
        /// The grouping decrease requests size.
        /// </summary>
        private GetTrieNodesRequest GetGroupedRequest(StateSyncBatch batch)
        {
            GetTrieNodesRequest request = new() { RootHash = batch.StateRoot };

            Dictionary<Hash256AsKey?, List<(TreePath path, StateSyncItem syncItem)>> itemsGroupedByAccount = [];
            List<(TreePath path, StateSyncItem syncItem)> accountTreePaths = [];

            foreach (StateSyncItem? item in batch.RequestedNodes)
            {
                if (item.Address is not null)
                {
                    if (!itemsGroupedByAccount.TryGetValue(item.Address, out List<(TreePath path, StateSyncItem syncItem)> storagePaths))
                    {
                        storagePaths = [];
                        itemsGroupedByAccount[item.Address] = storagePaths;
                    }

                    storagePaths.Add((item.Path, item));
                }
                else
                {
                    accountTreePaths.Add((item.Path, item));
                }
            }

            using DeferredRlpItemList.Builder builder = new();
            DeferredRlpItemList.Builder.Writer rootWriter = builder.BeginRootContainer();

            int requestedNodeIndex = 0;
            for (int i = 0; i < accountTreePaths.Count; i++)
            {
                (TreePath path, StateSyncItem syncItem) = accountTreePaths[i];
                using DeferredRlpItemList.Builder.Writer groupWriter = rootWriter.BeginContainer();
                groupWriter.WriteValue(Nibbles.EncodePath(path));

                // We validate the order of the response later and it has to be the same as RequestedNodes
                batch.RequestedNodes[requestedNodeIndex] = syncItem;
                requestedNodeIndex++;
            }

            foreach (KeyValuePair<Hash256AsKey?, List<(TreePath path, StateSyncItem syncItem)>> kvp in itemsGroupedByAccount)
            {
                using DeferredRlpItemList.Builder.Writer groupWriter = rootWriter.BeginContainer();
                groupWriter.WriteValue(kvp.Key?.Value.Bytes.ToArray());

                for (int groupIndex = 0; groupIndex < kvp.Value.Count; groupIndex++)
                {
                    (TreePath path, StateSyncItem syncItem) = kvp.Value[groupIndex];
                    groupWriter.WriteValue(Nibbles.EncodePath(path));

                    // We validate the order of the response later and it has to be the same as RequestedNodes
                    batch.RequestedNodes[requestedNodeIndex] = syncItem;
                    requestedNodeIndex++;
                }
            }

            rootWriter.Dispose();

            if (batch.RequestedNodes.Count != requestedNodeIndex)
            {
                Logger.Warn($"INCORRECT number of paths RequestedNodes.Length:{batch.RequestedNodes.Count} <> requestedNodeIndex:{requestedNodeIndex}");
            }

            request.AccountAndStoragePaths = new RlpPathGroupList(builder.ToRlpItemList());
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

            public void Initialize(IList<StateSyncItem> items) => _items = items;

            public void Reset() => _items = null;

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

            internal KeccakToValueKeccakList(HashList innerList) => _innerList = innerList;

            public IEnumerator<ValueHash256> GetEnumerator()
            {
                foreach (Hash256 keccak in _innerList)
                {
                    yield return keccak;
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public int Count => _innerList.Count;

            public ValueHash256 this[int index] => _innerList[index];
        }
    }
}
