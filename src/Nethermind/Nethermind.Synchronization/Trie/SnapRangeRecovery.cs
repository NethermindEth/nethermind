// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Tasks;
using Nethermind.Core.Utils;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;
#pragma warning disable CS9113 // Parameter is unread.
public class SnapRangeRecovery(ISyncPeerPool peerPool, ILogManager logManager): IPathRecovery
#pragma warning restore CS9113 // Parameter is unread.
{
    // Pick by reduced latency instead of throughput
    private static readonly IPeerAllocationStrategy SnapPeerStrategy =
        new SatelliteProtocolPeerAllocationStrategy<ISnapSyncPeer>(
            new BySpeedStrategy(TransferSpeedType.Latency, false),
            "snap");

    private const int ConcurrentAttempt = 3;
    private readonly ILogger _logger = logManager.GetClassLogger<SnapRangeRecovery>();

    private readonly AccountDecoder _accountDecoder = new();

    public async Task<IDictionary<TreePath, byte[]>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource();

        try
        {
            Task<IDictionary<TreePath, byte[]>>[] concurrentAttempts = Enumerable.Range(0, ConcurrentAttempt)
                .Select((_) =>
                {
                    return peerPool.AllocateAndRun(
                        (peer) =>
                        {
                            if (peer == null) return null;
                            try
                            {
                                return RecoverFromPeer(peer, rootHash, address, startingPath, startingNodeHash,
                                    fullPath,
                                    cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsDebug) _logger.Debug($"Error recovering node from {peer} {ex}");
                            }
                            return null;
                        }, SnapPeerStrategy, AllocationContexts.Snap, cts.Token);
                })
                .ToArray();

            return await Wait.ForPassingTask(
                result => result is not null,
                concurrentAttempts);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<IDictionary<TreePath, byte[]>?> RecoverFromPeer(
        ISyncPeer peer,
        Hash256 rootHash,
        Hash256? address,
        TreePath startingPath,
        Hash256 startingNodeHash,
        Hash256 fullPath,
        CancellationToken cancellationToken)
    {
        if (!peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out var snapProtocol)) return null;

        if (address == null)
        {
            AccountRange accountRange = new AccountRange(
                rootHash,
                fullPath,
                fullPath);

            AccountsAndProofs acc = await snapProtocol.GetAccountRange(accountRange, cancellationToken);
            if (acc.PathAndAccounts.Count == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Did not receive any path from {peer}.");
                return null;
            }
            if (acc.PathAndAccounts[0].Path != fullPath)
            {
                if (_logger.IsDebug) _logger.Debug($"Did not receive full path from {peer}. Received {acc.PathAndAccounts[0].Path} instead of {fullPath}");
                return null;
            }
            byte[] accountRlp = _accountDecoder.Encode(acc.PathAndAccounts[0].Account).Bytes;
            return AssembleResponse(startingNodeHash, startingPath, fullPath, accountRlp, acc.Proofs);
        }
        else
        {
            StorageRange storageRange = new StorageRange()
            {
                RootHash = rootHash,
                Accounts = new ArrayPoolList<PathWithAccount>(1)
                {
                    new PathWithAccount()
                    {
                        Path = address,
                    },
                },
                StartingHash = fullPath,
                LimitHash = startingNodeHash,
            };

            SlotsAndProofs res = await snapProtocol.GetStorageRange(storageRange, cancellationToken);
            if (res.PathsAndSlots.Count == 0 && res.PathsAndSlots[0].Count == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Did not receive any path from {peer}.");
                return null;
            }
            if (res.PathsAndSlots[0][0].Path != fullPath)
            {
                if (_logger.IsDebug) _logger.Debug($"Did not receive full path from {peer}. Received {res.PathsAndSlots[0][0].Path} instead of {fullPath}");
                return null;
            }
            return AssembleResponse(startingNodeHash, startingPath, fullPath, res.PathsAndSlots[0][0].SlotRlpValue, res.Proofs);
        }
    }

    private IDictionary<TreePath, byte[]>? AssembleResponse(
        Hash256 startingNodeHash,
        TreePath startingPath,
        Hash256 fullPath,
        byte[] value,
        IReadOnlyList<byte[]> proofs)
    {
        Dictionary<TreePath, byte[]> result = new Dictionary<TreePath, byte[]>();

        ITrieNodeResolver emptyResolver = new EmptyTrieNodeResolver();
        Dictionary<ValueHash256, byte[]> nodes = new Dictionary<ValueHash256, byte[]>();
        foreach (var proof in proofs)
        {
            nodes[ValueKeccak.Compute(proof)] = proof;
        }
        nodes[ValueKeccak.Compute(value)] = value;

        TreePath fullPathAsTreePath = new TreePath(fullPath, 64);
        Hash256 currentHash = startingNodeHash;
        TreePath currentPath = startingPath;
        while (nodes.TryGetValue(currentHash, out byte[] nodeRlp))
        {
            result[currentPath] = nodeRlp;

            TrieNode node = new TrieNode(NodeType.Unknown, nodeRlp);
            node.ResolveNode(emptyResolver, currentPath);

            if (_logger.IsTrace) _logger.Trace($"Traversing path {currentPath} with hash {currentHash}");

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
                    if (_logger.IsDebug) _logger.Debug($"Got unexpected final path {currentPath} instead of {fullPathAsTreePath}");
                    return null;
                }
                break;
            }
        }

        if (currentPath != fullPathAsTreePath)
        {
            TrieNode leafNode = TrieNodeFactory.CreateLeaf(fullPathAsTreePath.ToNibble()[currentPath.Length..], value);
            leafNode.ResolveNode(emptyResolver, currentPath);
            leafNode.ResolveKey(emptyResolver, ref currentPath, false);
            if (leafNode.Keccak != currentHash)
            {
                TrieNode n = new TrieNode(NodeType.Unknown, value);
                n.ResolveNode(emptyResolver, currentPath);
                leafNode.ResolveKey(emptyResolver, ref currentPath, false);
            }
            else
            {
                result[currentPath] = leafNode.FullRlp.ToArray();
            }
        }

        if (result.Count == 0)
        {
            return null;
        }
        return result;
    }

}
