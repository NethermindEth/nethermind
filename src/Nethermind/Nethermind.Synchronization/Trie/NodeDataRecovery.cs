// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Tasks;
using Nethermind.Core.Utils;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class NodeDataRecovery(ISyncPeerPool peerPool, INodeStorage nodeStorage, ILogManager logManager) : IPathRecovery
{
    private static readonly IPeerAllocationStrategy NodePeerStrategy =
        new CanServeByHashPeerAllocationStrategy(
            new BySpeedStrategy(TransferSpeedType.Latency, false));

    private const int ConcurrentAttempt = 3;
    private readonly ILogger _logger = logManager.GetClassLogger<NodeDataRecovery>();

    public async Task<IOwnedReadOnlyList<(TreePath, byte[])>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource();

        Hash256 currentHash = startingNodeHash;
        TreePath currentPath = startingPath;
        TreePath queryPath = new(fullPath, 64);

        // Sometimes the start path for the missing node and the actual full path that the trie is working on is not the same.
        // So we change the query to match the missing node path.
        if (!queryPath.StartsWith(startingPath))
        {
            queryPath = startingPath.Append(0, 64 - startingPath.Length);
        }

        ArrayPoolList<(TreePath, byte[])> recoveredNodes = new(1);
        do
        {
            // In case of deeper node that already exist.
            byte[]? nodeRlp = nodeStorage.Get(address, currentPath, currentHash);
            if (nodeRlp is null)
            {
                nodeRlp = await FetchRlp(rootHash, address, currentPath, currentHash, cts.Token);
            }

            if (nodeRlp is null)
            {
                if (_logger.IsDebug) _logger.Debug($"Failed to fetch complete path when recovering {fullPath}. Fetched nodes: {recoveredNodes.Count}.");
                return null;
            }

            recoveredNodes.Add((currentPath, nodeRlp));

            TrieNode node = new(NodeType.Unknown, nodeRlp);
            node.ResolveNode(EmptyTrieNodeResolver.Instance, currentPath);

            if (node.IsBranch)
            {
                int childIndex = queryPath[currentPath.Length];
                currentHash = node.GetChildHash(childIndex);
                currentPath.AppendMut(childIndex);
            }
            else if (node.IsExtension)
            {
                currentHash = node.GetChildHash(1);
                currentPath = currentPath.Append(node.Key);
            }
            else if (node.IsLeaf)
            {
                break;
            }

        } while (currentHash is not null);

        return recoveredNodes;
    }

    private async Task<byte[]?> FetchRlp(Hash256 rootHash, Hash256? address, TreePath path, Hash256 hash, CancellationToken cancellationToken)
    {
        using ArrayPoolList<Task<byte[]?>> tasks = Enumerable.Range(0, ConcurrentAttempt)
            .Select(_ =>
            {
                return peerPool.AllocateAndRun(async (peer) =>
                {
                    if (peer is null) return null;
                    try
                    {
                        byte[]? res = await RecoverNodeFromPeer(peer.SyncPeer, rootHash, address, path, hash, cancellationToken);
                        if (res is null) peerPool.ReportWeakPeer(peer, AllocationContexts.State);
                        return res;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Error recovering node from {peer} {ex}");
                        peerPool.ReportWeakPeer(peer, AllocationContexts.State);
                    }

                    return null;
                }, NodePeerStrategy, AllocationContexts.State, cancellationToken);
            })
            .ToPooledList(ConcurrentAttempt);

        return await Wait.AnyWhere(
            result => result is not null,
            tasks);
    }

    private async Task<byte[]?> RecoverNodeFromPeer(ISyncPeer syncPeer, Hash256 rootHash, Hash256? address, TreePath treePath, Hash256 hash, CancellationToken cancellationToken)
    {
        if (syncPeer.ProtocolVersion < EthVersions.Eth67)
        {
            if (_logger.IsTrace) _logger.Trace($"Fetching H {hash} P {treePath} from {syncPeer} via eth");
            IOwnedReadOnlyList<byte[]>? data = await syncPeer.GetNodeData([hash], cancellationToken);
            if (data?.Count > 0 && Keccak.Compute(data[0]) == hash)
            {
                return data[0];
            }
        }
        else if (syncPeer.TryGetSatelliteProtocol(Protocol.NodeData, out INodeDataPeer nodeDataPeer))
        {
            if (_logger.IsTrace) _logger.Trace($"Fetching H {hash} P {treePath} from {syncPeer} via nodedata");
            IOwnedReadOnlyList<byte[]>? data = await nodeDataPeer.GetNodeData([hash], cancellationToken);
            if (data?.Count > 0 && Keccak.Compute(data[0]) == hash)
            {
                return data[0];
            }
        }
        else if (syncPeer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer snapSyncPeer))
        {
            if (_logger.IsTrace) _logger.Trace($"Fetching H {hash} P {treePath} from {syncPeer} via snap");
            PathGroup group;
            if (address is null)
            {
                group = new PathGroup
                {
                    Group = [Nibbles.EncodePath(treePath)]
                };
            }
            else
            {
                group = new PathGroup
                {
                    Group = [address.Bytes.ToArray(), Nibbles.EncodePath(treePath)]
                };
            }

            using IOwnedReadOnlyList<byte[]>? item = await snapSyncPeer.GetTrieNodes(new GetTrieNodesRequest()
            {
                RootHash = rootHash,
                AccountAndStoragePaths = new ArrayPoolList<PathGroup>(1)
                { group },
            }, cancellationToken);

            if (item is not null && item.Count > 0)
            {
                return item[0];
            }
        }

        return null;
    }
}
