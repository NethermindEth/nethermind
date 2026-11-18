// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
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

/// <summary>Fetches missing trie paths from hash-anchored snap range responses.</summary>
public class SnapRangeRecovery(ISyncPeerPool peerPool, ILogManager logManager, INodeStatsManager? nodeStatsManager) : IPathRecovery
{
    /// <summary>Creates path recovery without transfer speed reporting.</summary>
    public SnapRangeRecovery(ISyncPeerPool peerPool, ILogManager logManager) : this(peerPool, logManager, null)
    {
    }

    private sealed record RecoveryResult(PeerInfo Peer, IOwnedReadOnlyList<(TreePath, byte[])> Nodes, long ElapsedMilliseconds) : IOwnedReadOnlyList<(TreePath, byte[])>
    {
        public int Count => Nodes.Count;
        public (TreePath, byte[]) this[int index] => Nodes[index];
        public ReadOnlySpan<(TreePath, byte[])> AsSpan() => Nodes.AsSpan();
        public IEnumerator<(TreePath, byte[])> GetEnumerator() => Nodes.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Nodes.Dispose();
    }

    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    // Pick by reduced latency instead of throughput
    private static readonly IPeerAllocationStrategy SnapPeerStrategy =
        new SatelliteProtocolPeerAllocationStrategy<ISnapSyncPeer>(
            new BySpeedStrategy(TransferSpeedType.Latency, false),
            Protocol.Snap);

    private static readonly AccountDecoder AccountRlpDecoder = AccountDecoder.Instance;

    private const int ConcurrentAttempt = 3;
    private readonly ILogger _logger = logManager.GetClassLogger<SnapRangeRecovery>();

    /// <inheritdoc/>
    public Task<IOwnedReadOnlyList<(TreePath, byte[])>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken) =>
        Recover(rootHash, address, startingPath, startingNodeHash, fullPath, cancellationToken, reportSpeed: true);

    internal void ReportAccepted(IOwnedReadOnlyList<(TreePath, byte[])> nodes)
    {
        if (nodes is not RecoveryResult recovered) return;
        long bytes = 0;
        foreach ((TreePath _, byte[] node) in recovered.Nodes) bytes += node.Length;
        if (bytes > 0)
            nodeStatsManager?.ReportTransferSpeedEvent(recovered.Peer.SyncPeer.Node, TransferSpeedType.SnapRanges,
                bytes / Math.Max(1, recovered.ElapsedMilliseconds));
    }

    internal async Task<IOwnedReadOnlyList<(TreePath, byte[])>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken, bool reportSpeed)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource();
        CancellationToken token = cts.Token;

        Task<RecoveryResult?>[] concurrentAttempts = new Task<RecoveryResult?>[ConcurrentAttempt];
        for (int i = 0; i < concurrentAttempts.Length; i++) concurrentAttempts[i] = Attempt();

        RecoveryResult? recovered = await Wait.AnyWhere(static result => result is not null, concurrentAttempts);
        if (token.IsCancellationRequested)
        {
            recovered?.Dispose();
            return null;
        }
        if (recovered is null) return null;

        if (reportSpeed) ReportAccepted(recovered);
        return recovered;

        // Completes with null rather than faulting. Wait.AnyWhere rethrows the first faulted task and
        // drops the rest, and Allocate/Free throw outside the reach of the inner handler.
        async Task<RecoveryResult?> Attempt()
        {
            try
            {
                return await peerPool.AllocateAndRun(async (peer) =>
                {
                    try
                    {
                        long started = TimeProvider.GetTimestamp();
                        IOwnedReadOnlyList<(TreePath, byte[])>? result = await RecoverFromPeer(peer.SyncPeer, rootHash, address, startingPath, startingNodeHash,
                            fullPath,
                            token);
                        if (token.IsCancellationRequested)
                        {
                            result?.Dispose();
                            return null;
                        }
                        if (result is not null)
                            return new RecoveryResult(peer, result, (long)TimeProvider.GetElapsedTime(started).TotalMilliseconds);

                        if (_logger.IsDebug) _logger.Debug($"Mark peer {peer:hide} weak");
                        peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Error recovering node from {peer:hide} {ex:hide}");
                        peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                    }
                    return null;
                }, SnapPeerStrategy, AllocationContexts.Snap, token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"Error recovering path {address ?? Hash256.Zero}:{fullPath} {ex}");
                return null;
            }
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
        if (!peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer snapProtocol)) return null;

        // Sometimes the start path for the missing node and the actual full path that the trie is working on is not the same.
        // So we change the query to match the missing node path.
        TreePath queryPathTreePath = new(queryPath, 64);
        if (!queryPathTreePath.StartsWith(startingPath))
        {
            queryPath = startingPath.Append(0, 64 - startingPath.Length).Path;
        }

        if (address == null)
        {
            AccountRange accountRange = new(
                rootHash,
                queryPath,
                queryPath);

            using AccountsAndProofs acc = await snapProtocol.GetAccountRange(accountRange, cancellationToken);
            if (acc.PathAndAccounts.Count == 0 && acc.Proofs.Count == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Did not receive any path from {peer:hide}. {acc.Proofs.Count}");
                return null;
            }

            ReadOnlySpan<PathWithAccount> pathAndAccounts = acc.PathAndAccounts.AsSpan();
            byte[] accountRlp = [];
            ValueHash256 slotPath = default;
            if (pathAndAccounts.Length > 0)
            {
                accountRlp = AccountRlpDecoder.Encode(pathAndAccounts[0].Account);
                slotPath = pathAndAccounts[0].Path;
            }

            return AssembleResponse(startingNodeHash, startingPath, slotPath, accountRlp, acc.Proofs);
        }
        else
        {
            using StorageRange storageRange = new()
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

            using SlotsAndProofs res = await snapProtocol.GetStorageRange(storageRange, cancellationToken);
            ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> pathsAndSlots = res.PathsAndSlots.AsSpan();
            ReadOnlySpan<PathWithStorageSlot> firstSlots = pathsAndSlots.Length == 0 ? [] : pathsAndSlots[0].AsSpan();
            if ((pathsAndSlots.Length == 0 || firstSlots.Length == 0) && res.Proofs.Count == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Did not receive any path from {peer:hide}. {res.Proofs.Count}");
                return null;
            }

            byte[] slotRlp = [];
            ValueHash256 slotPath = default;
            if (firstSlots.Length > 0)
            {
                slotRlp = firstSlots[0].SlotRlpValue;
                slotPath = firstSlots[0].Path;
            }

            return AssembleResponse(startingNodeHash, startingPath, slotPath, slotRlp, res.Proofs);
        }
    }

    private IOwnedReadOnlyList<(TreePath, byte[])>? AssembleResponse(
        Hash256 startingNodeHash,
        TreePath startingPath,
        in ValueHash256 slotPath,
        byte[] value,
        IByteArrayList proofs)
    {
        ArrayPoolList<(TreePath, byte[])> result = [with(1)];

        try
        {
            ITrieNodeResolver emptyResolver = new EmptyTrieNodeResolver();
            Dictionary<ValueHash256, byte[]> nodes = [];
            for (int i = 0; i < proofs.Count; i++)
            {
                byte[] proof = proofs[i].ToArray();
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

                if (!nodes.TryGetValue(currentHash, out byte[]? rlp))
                {
                    if (slotPathAsTreePath.Truncate(currentPath.Length) == currentPath)
                    {
                        // Try using the slot as a leaf with the remaining path as key
                        TrieNode leafNode = TrieNodeFactory.CreateLeaf(slotPathAsTreePath.ToNibble()[currentPath.Length..], new CappedArray<byte>(value));
                        leafNode.ResolveNode(emptyResolver, currentPath);
                        leafNode.ResolveKey(emptyResolver, ref currentPath);
                        if (leafNode.Keccak == currentHash)
                        {
                            rlp = leafNode.FullRlp.ToArray();
                        }
                    }
                }

                // Eh.. what can you do?
                if (rlp == null) continue;

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

            if (result.Count == 0)
            {
                result.Dispose();
                return null;
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

}
