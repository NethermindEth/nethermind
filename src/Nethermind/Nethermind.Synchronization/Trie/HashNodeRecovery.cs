// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Healing;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HashNodeRecovery(ISyncPeerPool peerPool, INodeStorage nodeStorage): IPathRecovery
{
    private static readonly IPeerAllocationStrategy SnapPeerStrategy =
        new CanServeByHashPeerAllocationStrategy(
            new BySpeedStrategy(TransferSpeedType.Latency, false));

    public async Task<IDictionary<TreePath, byte[]>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath)
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10000));

        Hash256 currentHash = startingNodeHash;
        TreePath currentPath = startingPath;
        TreePath fullPathAsTreePath = new TreePath(fullPath, 64);

        Dictionary<TreePath, byte[]> recoveredNodes = new();

        do
        {
            byte[]? nodeRlp = await FetchRlp(address, currentPath, currentHash, cts.Token);
            if (nodeRlp == null)
            {
                // Failure!!!!
                return null;
            }

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
                    Console.Error.WriteLine($"Got unexpected final path {currentPath} instead of {fullPathAsTreePath}");
                    return null;
                }
                break;
            }

        } while (currentPath != fullPathAsTreePath);

        return recoveredNodes;
    }

    private async Task<byte[]?> FetchRlp(Hash256? address, TreePath path, Hash256 hash, CancellationToken cancellationToken)
    {
        // In case of deeper node that already exist.
        byte[]? rlp = nodeStorage.Get(address, path, hash);
        if (rlp is not null) return rlp;

        try
        {
            // TODO: Multiple pear, catch exception on each of them separately.
            return await peerPool.AllocateAndRun((peer) =>
            {
                return RecoverNodeFromPeer(peer, hash, cancellationToken);
            }, SnapPeerStrategy, AllocationContexts.State, cancellationToken);
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
            if (data?.Count > 0)
            {
                return data[0];
            }
        }
        else if (syncPeer.TryGetSatelliteProtocol(Protocol.NodeData, out INodeDataPeer nodeDataPeer))
        {
            IOwnedReadOnlyList<byte[]>? data = await nodeDataPeer.GetNodeData([hash], cancellationToken);
            if (data?.Count > 0)
            {
                return data[0];
            }
        }

        return null;
    }
}
