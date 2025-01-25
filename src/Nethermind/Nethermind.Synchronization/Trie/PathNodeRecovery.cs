// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
public class PathNodeRecovery(ISyncPeerPool peerPool, ILogManager logManager): IPathRecovery
#pragma warning restore CS9113 // Parameter is unread.
{
    // Pick by reduced latency instead of throughput
    private static readonly IPeerAllocationStrategy SnapPeerStrategy =
        new SatelliteProtocolPeerAllocationStrategy<ISnapSyncPeer>(
            new BySpeedStrategy(TransferSpeedType.Latency, false),
            "snap");

    private readonly AccountDecoder _accountDecoder = new();

    public async Task<IDictionary<TreePath, byte[]>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath)
    {
        Console.Error.WriteLine($"Trying to recovery {address} {startingPath}");
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10000));

        try
        {
            Console.Error.WriteLine($"Cancel token is {cts.Token.GetHashCode()}");
            return await peerPool.AllocateAndRun((peer) =>
            {
                Console.Error.WriteLine($"Call {peer} to recover");
                // TODO: Catch all exception
                return RecoverFromPeer(peer, rootHash, address, startingPath, startingNodeHash, fullPath, cts.Token);
            }, SnapPeerStrategy, AllocationContexts.Snap, cts.Token);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Thrown {e}");
            throw;
        }
        finally
        {
            Console.Error.WriteLine($"Done");
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
            Console.Error.WriteLine($"Account {rootHash}");
            AccountRange accountRange = new AccountRange(
                rootHash,
                fullPath,
                fullPath);

            AccountsAndProofs acc = await snapProtocol.GetAccountRange(accountRange, cancellationToken);
            if (acc.PathAndAccounts.Count == 0) return null;
            if (acc.PathAndAccounts[0].Path != fullPath)
            {
                Console.Error.WriteLine($"Account {acc.PathAndAccounts[0].Path} is different from {fullPath}");
                return null;
            }
            byte[] accountRlp = _accountDecoder.Encode(acc.PathAndAccounts[0].Account).Bytes;
            Console.Error.WriteLine($"Account path is {acc.PathAndAccounts[0].Path} {acc.PathAndAccounts.Count} {startingPath}");
            return AssembleResponse(startingNodeHash, startingPath, fullPath, accountRlp, acc.Proofs);
        }
        else
        {
            Console.Error.WriteLine("Storage");
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
            if (res.PathsAndSlots.Count == 0 && res.PathsAndSlots[0].Count == 0) return null;
            if (res.PathsAndSlots[0][0].Path != fullPath)
            {
                Console.Error.WriteLine($"Account {res.PathsAndSlots[0][0].Path} is different from {fullPath}");
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
        Console.Error.WriteLine($"Assemble response with full {fullPath}");
        Dictionary<TreePath, byte[]> result = new Dictionary<TreePath, byte[]>();

        ITrieNodeResolver emptyResolver = new EmptyTrieNodeResolver();
        Dictionary<ValueHash256, byte[]> nodes = new Dictionary<ValueHash256, byte[]>();
        foreach (var proof in proofs)
        {
            TrieNode n = new TrieNode(NodeType.Unknown, proof);
            n.ResolveNode(emptyResolver, TreePath.Empty);
            Console.Error.WriteLine($"Proof node path {ValueKeccak.Compute(proof)} {n.NodeType} {(n.Key != null ? TreePath.FromNibble(n.Key) : "null")}");
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

            Console.Error.WriteLine($"On {currentPath} with type {node.NodeType}");
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
                Console.Error.WriteLine($"Node decode has key {n.Key} {n.NodeType}");
            }
            else
            {
                Console.Error.WriteLine("Seems right");
                result[currentPath] = leafNode.FullRlp.ToArray();
            }
        }

        Console.Error.WriteLine($"Assembled {result.Count} response");
        if (result.Count == 0)
        {
            Console.Error.WriteLine($"Unable to find expected nodes within returned data");
            return null;
        }
        return result;
    }

    private class EmptyTrieNodeResolver : ITrieNodeResolver
    {
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            throw new InvalidOperationException("Empty node resolver should not be called");
        }

        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            throw new InvalidOperationException("Empty node resolver should not be called");
        }

        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            throw new InvalidOperationException("Empty node resolver should not be called");
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
        {
            throw new InvalidOperationException("Empty node resolver should not be called");
        }

        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.Hash;
    }
}
