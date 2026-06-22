// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Walks the trie once and captures trie-node RLP along the path to each of N target accounts and,
/// by descending into each touched account's storage trie at its leaf, along the path to each of
/// that account's touched slots. Storage-trie nodes are discriminated per account via
/// <c>ctx.Storage</c>, which carries the owning account's path commitment (keccak(address)).
/// </summary>
internal sealed class MultiAccountProofCollector : ITreeVisitor<TreePathContextWithStorage>
{
    private readonly ValueHash256[] _accountHashes;
    // Sorted keccak(slot) targets per account, keyed by keccak(address) (= ctx.Storage at the descent).
    private readonly Dictionary<ValueHash256, ValueHash256[]> _slotHashesByAccount;
    private readonly List<byte[]> _nodes;

    public IReadOnlyList<byte[]> Nodes => _nodes;

    public MultiAccountProofCollector(IReadOnlyDictionary<AddressAsKey, HashSet<UInt256>> storageSlots)
    {
        int n = storageSlots.Count;
        _accountHashes = new ValueHash256[n];
        _slotHashesByAccount = new Dictionary<ValueHash256, ValueHash256[]>(n, GenericEqualityComparer.GetOptimized<ValueHash256>());
        int i = 0;
        int totalSlots = 0;
        Span<byte> slotKey = stackalloc byte[32];
        foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> entry in storageSlots)
        {
            ValueHash256 accountHash = ValueKeccak.Compute(entry.Key.Value.Bytes);
            _accountHashes[i++] = accountHash;

            if (entry.Value.Count == 0) continue;
            totalSlots += entry.Value.Count;
            ValueHash256[] slotHashes = new ValueHash256[entry.Value.Count];
            int j = 0;
            foreach (UInt256 slot in entry.Value)
            {
                slot.ToBigEndian(slotKey);
                slotHashes[j++] = ValueKeccak.Compute(slotKey);
            }
            Array.Sort(slotHashes);
            _slotHashesByAccount[accountHash] = slotHashes;
        }

        // Sorted so ShouldVisit can binary search instead of scanning every hash. The scan is
        // O(targets) per visited child, which dominates block-scale walks (thousands of accounts).
        Array.Sort(_accountHashes);

        // Capacity hint: one trie path of typical depth per touched account and slot.
        _nodes = new List<byte[]>(Math.Max(16, n * 8 + totalSlots * 4));
    }

    public bool IsFullDbScan => false;

    public bool ShouldVisit(in TreePathContextWithStorage ctx, in ValueHash256 nextNode)
    {
        // Inside a storage trie, filter by the owning account's touched slots. The descent itself
        // is gated on the account leaf's full path matching a target, so an untouched account's
        // storage is never entered.
        if (ctx.Storage is not null)
        {
            return _slotHashesByAccount.TryGetValue(ctx.Storage, out ValueHash256[]? slotHashes)
                && HasTargetWithPrefix(slotHashes, ctx.Path);
        }

        return HasTargetWithPrefix(_accountHashes, ctx.Path);
    }

    private static bool HasTargetWithPrefix(ValueHash256[] sortedHashes, in TreePath path)
    {
        // Hashes with the path as nibble-prefix form one contiguous run in the sorted array, and
        // the run starts at the first hash >= the zero-padded path (any later non-member exceeds
        // the path in a prefix nibble) — so the lower-bound element alone decides membership.
        int index = Array.BinarySearch(sortedHashes, path.ToLowerBoundPath());
        if (index < 0) index = ~index;
        return index < sortedHashes.Length && IsPrefix(sortedHashes[index].Bytes, path);
    }

    public void VisitTree(in TreePathContextWithStorage ctx, in ValueHash256 rootHash) { }

    public void VisitMissingNode(in TreePathContextWithStorage ctx, in ValueHash256 nodeHash) { }

    public void VisitBranch(in TreePathContextWithStorage ctx, TrieNode node) => AddProofItem(node);

    public void VisitExtension(in TreePathContextWithStorage ctx, TrieNode node) => AddProofItem(node);

    public void VisitLeaf(in TreePathContextWithStorage ctx, TrieNode node) => AddProofItem(node);

    public void VisitAccount(in TreePathContextWithStorage ctx, TrieNode node, in AccountStruct account) { }

    private void AddProofItem(TrieNode node)
    {
        if (node.Keccak is null) return;
        _nodes.Add(node.FullRlp.ToArray());
    }

    private static bool IsPrefix(ReadOnlySpan<byte> target, in TreePath currentPath)
    {
        int length = currentPath.Length;
        if (length > target.Length * 2) return false;
        for (int i = 0; i < length; i++)
        {
            int targetNibble = (i & 1) == 0 ? target[i >> 1] >> 4 : target[i >> 1] & 0x0F;
            if (currentPath[i] != targetNibble) return false;
        }
        return true;
    }
}
