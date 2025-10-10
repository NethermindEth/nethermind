// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Buffers;
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
public class SnapRangeRecovery(ISyncPeerPool peerPool, ILogManager logManager) : IPathRecovery
{
    // Pick by reduced latency instead of throughput
    private static readonly IPeerAllocationStrategy SnapPeerStrategy =
        new SatelliteProtocolPeerAllocationStrategy<ISnapSyncPeer>(
            new BySpeedStrategy(TransferSpeedType.Latency, false),
            Protocol.Snap);

    private const int ConcurrentAttempt = 3;
    private readonly ILogger _logger = logManager.GetClassLogger<SnapRangeRecovery>();

    private readonly AccountDecoder _accountDecoder = new();

    public async Task<IOwnedReadOnlyList<(TreePath, byte[])>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource();

        try
        {
            using ArrayPoolList<Task<IOwnedReadOnlyList<(TreePath, byte[])>>>? concurrentAttempts = Enumerable.Range(0, ConcurrentAttempt)
                .Select(_ =>
                {
                    return peerPool.AllocateAndRun(async (peer) =>
                        {
                            if (peer is null) return null;
                            try
                            {
                                IOwnedReadOnlyList<(TreePath, byte[])>? result = await RecoverFromPeer(peer.SyncPeer, rootHash, address, startingPath, startingNodeHash,
                                    fullPath,
                                    cts.Token);
                                if (result is not null) return result;

                                if (_logger.IsDebug) _logger.Debug($"Mark peer {peer} weak");
                                peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsWarn) _logger.Warn($"Error recovering node from {peer} {ex}");
                                peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                            }
                            return null;
                        }, SnapPeerStrategy, AllocationContexts.Snap, cts.Token);
                })
                .ToPooledList(ConcurrentAttempt);

            return await Wait.AnyWhere(
                result => result is not null,
                concurrentAttempts);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<IOwnedReadOnlyList<(TreePath, byte[])>?> RecoverFromPeer(
        ISyncPeer peer,
        Hash256 rootHash,
        Hash256? address,
        TreePath startingPath,
        Hash256 startingNodeHash,
        ValueHash256 queryPath,
        CancellationToken cancellationToken)
    {
        if (!peer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer? snapProtocol)) return null;

        // Sometimes the start path for the missing node and the actual full path that the trie is working on is not the same.
        // So we change the query to match the missing node path.
        TreePath queryPathTreePath = new(queryPath, 64);
        if (!queryPathTreePath.StartsWith(startingPath))
        {
            queryPath = startingPath.Append(0, 64 - startingPath.Length).Path;
        }

        if (address is null)
        {
            AccountRange accountRange = new(
                rootHash,
                queryPath,
                queryPath);

            AccountsAndProofs acc = await snapProtocol.GetAccountRange(accountRange, cancellationToken);
            if (acc.PathAndAccounts.Count == 0 && acc.Proofs.Count == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Did not receive any path from {peer}. {acc.Proofs.Count}");
                return null;
            }

            byte[] accountRlp = [];
            ValueHash256 slotPath = default;
            if (acc.PathAndAccounts.Count > 0)
            {
                accountRlp = _accountDecoder.Encode(acc.PathAndAccounts[0].Account).Bytes;
                slotPath = acc.PathAndAccounts[0].Path;
            }

            return AssembleResponse(startingNodeHash, startingPath, slotPath, accountRlp, acc.Proofs);
        }
        else
        {
            StorageRange storageRange = new()
            {
                RootHash = rootHash,
                Accounts = new ArrayPoolList<PathWithAccount>(1)
                {
                    new()
                    {
                        Path = address,
                    },
                },
                StartingHash = queryPath,
                LimitHash = queryPath,
            };

            SlotsAndProofs res = await snapProtocol.GetStorageRange(storageRange, cancellationToken);
            if ((res.PathsAndSlots.Count == 0 || res.PathsAndSlots[0].Count == 0) && res.Proofs.Count == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Did not receive any path from {peer}. {res.Proofs.Count}");
                return null;
            }

            byte[] slotRlp = [];
            ValueHash256 slotPath = default;
            if (res.PathsAndSlots.Count > 0 && res.PathsAndSlots[0].Count > 0)
            {
                slotRlp = res.PathsAndSlots[0][0].SlotRlpValue;
                slotPath = res.PathsAndSlots[0][0].Path;
            }

            return AssembleResponse(startingNodeHash, startingPath, slotPath, slotRlp, res.Proofs);
        }
    }

    private IOwnedReadOnlyList<(TreePath, byte[])>? AssembleResponse(
        Hash256 startingNodeHash,
        TreePath startingPath,
        in ValueHash256 slotPath,
        byte[] value,
        IReadOnlyList<byte[]> proofs)
    {
        ArrayPoolList<(TreePath, byte[])> result = new(1);

        ITrieNodeResolver emptyResolver = new EmptyTrieNodeResolver();
        Dictionary<ValueHash256, byte[]> nodes = new();
        foreach (byte[] proof in proofs)
        {
            nodes[ValueKeccak.Compute(proof)] = proof;
        }
        nodes[ValueKeccak.Compute(value)] = value;

        TreePath slotPathAsTreePath = new(slotPath, 64);
        Stack<(TreePath, Hash256)> checkStack = new();
        checkStack.Push((startingPath, startingNodeHash));

        while (checkStack.TryPop(out (TreePath, Hash256) item))
        {
            TreePath currentPath = item.Item1;
            Hash256 currentHash = item.Item2;

            if (!nodes.TryGetValue(currentHash, out byte[] rlp))
            {
                if (slotPathAsTreePath.Truncate(currentPath.Length) == currentPath)
                {
                    // Try using the slot as a leaf with the remaining path as key
                    TrieNode leafNode = TrieNodeFactory.CreateLeaf(slotPathAsTreePath.ToNibble()[currentPath.Length..], new SpanSource(value));
                    leafNode.ResolveNode(emptyResolver, currentPath);
                    leafNode.ResolveKey(emptyResolver, ref currentPath);
                    if (leafNode.Keccak == currentHash)
                    {
                        rlp = leafNode.FullRlp.ToArray();
                    }
                }
            }

            // Eh.. what can you do?
            if (rlp is null) continue;

            result.Add((currentPath, rlp));

            TrieNode node = new(NodeType.Unknown, rlp);
            node.ResolveNode(emptyResolver, currentPath);

            if (_logger.IsTrace) _logger.Trace($"Traversing path {currentPath} with hash {currentHash}");

            if (node.IsBranch)
            {
                for (int i = 0; i < 16; i++)
                {
                    Hash256? childHash = node.GetChildHash(i);
                    if (childHash == null) continue;
                    checkStack.Push((currentPath.Append(i), childHash));
                }
            }
            else if (node.IsExtension)
            {
                checkStack.Push((currentPath.Append(node.Key), node.GetChildHash(1)));
            }
            else if (node.IsLeaf)
            {
            }
        }

        return result.Count == 0 ? null : result;
    }

}
