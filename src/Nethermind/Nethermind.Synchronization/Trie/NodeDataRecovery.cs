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
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class NodeDataRecovery(ISyncPeerPool peerPool, INodeStorage nodeStorage, ILogManager logManager): IPathRecovery
{
    private static readonly IPeerAllocationStrategy NodePeerStrategy =
        new CanServeByHashPeerAllocationStrategy(
            new BySpeedStrategy(TransferSpeedType.Latency, false));

    private const int ConcurrentAttempt = 3;
    private readonly ILogger _logger = logManager.GetClassLogger<NodeDataRecovery>();

    public async Task<IDictionary<TreePath, byte[]>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource();

        Hash256 currentHash = startingNodeHash;
        TreePath currentPath = startingPath;
        TreePath fullPathAsTreePath = new TreePath(fullPath, 64);

        Dictionary<TreePath, byte[]> recoveredNodes = new();

        do
        {
            // In case of deeper node that already exist.
            byte[]? nodeRlp = nodeStorage.Get(address, currentPath, currentHash);
            if (nodeRlp is null)
            {
                nodeRlp = await FetchRlp(address, currentPath, currentHash, cts.Token);
            }

            if (nodeRlp == null)
            {
                if (_logger.IsDebug) _logger.Debug($"Failed to fetch complete path when recovering {fullPath}. Fetched nodes: {recoveredNodes.Count}.");
                return null;
            }

            recoveredNodes[currentPath] = nodeRlp;

            TrieNode? node = new TrieNode(NodeType.Unknown, nodeRlp);
            node.ResolveNode(EmptyTrieNodeResolver.Instance, currentPath);

            if (node.IsBranch)
            {
                int childIndex = fullPathAsTreePath[currentPath.Length];
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
                currentPath = currentPath.Append(node.Key);
                if (currentPath != fullPathAsTreePath)
                {
                    return null;
                }
                break;
            }

        } while (currentPath != fullPathAsTreePath);

        return recoveredNodes;
    }

    private async Task<byte[]?> FetchRlp(Hash256? address, TreePath path, Hash256 hash, CancellationToken cancellationToken)
    {
        try
        {
            Task<byte[]?>[] tasks = Enumerable.Range(0, ConcurrentAttempt)
                .Select(_ =>
                {
                    return peerPool.AllocateAndRun((peer) =>
                    {
                        if (peer == null) return null;
                        try
                        {
                            return RecoverNodeFromPeer(peer, hash, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Error recovering node from {peer} {ex}");
                        }

                        return null;
                    }, NodePeerStrategy, AllocationContexts.State, cancellationToken);
                })
                .ToArray();

            return await Wait.ForPassingTask(
                result => result is not null,
                tasks);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<byte[]?> RecoverNodeFromPeer(ISyncPeer syncPeer, Hash256 hash, CancellationToken cancellationToken)
    {
        if (syncPeer.ProtocolVersion < EthVersions.Eth67)
        {
            IOwnedReadOnlyList<byte[]>? data = await syncPeer.GetNodeData([hash], cancellationToken);
            if (data?.Count > 0 && Keccak.Compute(data[0]) == hash)
            {
                return data[0];
            }
        }
        else if (syncPeer.TryGetSatelliteProtocol(Protocol.NodeData, out INodeDataPeer nodeDataPeer))
        {
            IOwnedReadOnlyList<byte[]>? data = await nodeDataPeer.GetNodeData([hash], cancellationToken);
            if (data?.Count > 0 && Keccak.Compute(data[0]) == hash)
            {
                return data[0];
            }
        }

        return null;
    }
}
