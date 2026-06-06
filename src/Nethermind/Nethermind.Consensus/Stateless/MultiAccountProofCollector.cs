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
/// Tree visitor that walks the state trie once, collecting trie-node RLP along the path to each of N
/// target accounts and their requested storage slots. Replaces N independent per-account
/// <see cref="State.Proofs.AccountProofCollector"/> walks (O(K × depth) → O(unique nodes on the path
/// union)) for the witness path. Storage tries stay per-account, dispatched via <c>ctx.Storage</c>.
/// Output is a flat node list; the consumer deduplicates.
/// </summary>
internal sealed class MultiAccountProofCollector : ITreeVisitor<TreePathContextWithStorage>
{
    // Targets are the hashed keys themselves (32 bytes = 64 nibbles); ShouldVisit reads nibbles straight
    // from these, so no Nibble[] is expanded per account/slot and the hashes stay as value types.
    private readonly ValueHash256[] _accountHashes;
    // Parallel to _accountHashes; true if the account had a slot requested. Lets ShouldVisit skip the
    // storage subtree for accounts with no touched slots (avoids capturing nodes the verifier won't need).
    private readonly bool[] _hasSlots;
    // Keyed by the hashed address (== TreePathContextWithStorage.Storage). The framework hands us the
    // address-hash, never the original Address, when traversing the storage subtrie.
    private readonly Dictionary<ValueHash256, ValueHash256[]> _slotsByAddress;
    private readonly List<byte[]> _nodes;

    public IReadOnlyList<byte[]> Nodes => _nodes;

    public MultiAccountProofCollector(IReadOnlyDictionary<AddressAsKey, HashSet<UInt256>> storageSlots)
    {
        int n = storageSlots.Count;
        _accountHashes = new ValueHash256[n];
        _hasSlots = new bool[n];
        _slotsByAddress = new Dictionary<ValueHash256, ValueHash256[]>(n, GenericEqualityComparer.GetOptimized<ValueHash256>());

        Span<byte> slotBuf = stackalloc byte[32];
        int i = 0;
        int totalSlots = 0;
        foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> entry in storageSlots)
        {
            ValueHash256 addressHash = ValueKeccak.Compute(entry.Key.Value.Bytes);
            _accountHashes[i] = addressHash;
            int slotCount = entry.Value.Count;
            _hasSlots[i] = slotCount > 0;
            i++;
            totalSlots += slotCount;

            if (slotCount == 0) continue;

            ValueHash256[] slotHashes = new ValueHash256[slotCount];
            int j = 0;
            foreach (UInt256 slot in entry.Value)
            {
                slot.ToBigEndian(slotBuf);
                slotHashes[j++] = ValueKeccak.Compute(slotBuf);
            }
            _slotsByAddress[addressHash] = slotHashes;
        }

        // Pre-size for the branch/extension/leaf nodes captured along the path union (≈ a short chain
        // per touched account and slot) so the list doesn't resize repeatedly during the walk.
        _nodes = new List<byte[]>(Math.Max(16, (n + totalSlots) * 2));
    }

    public bool IsFullDbScan => false;

    public bool ShouldVisit(in TreePathContextWithStorage ctx, in ValueHash256 nextNode)
    {
        if (ctx.Storage is null)
        {
            // State trie: descend if any target's account hash passes through the current node AND
            // either we haven't yet reached that target's leaf (still descending) or that target has
            // slots to descend into. Linear in K per node; fine for the typical K (5–50 touched accounts).
            for (int i = 0; i < _accountHashes.Length; i++)
            {
                ReadOnlySpan<byte> accountHash = _accountHashes[i].Bytes;
                if (IsPrefix(accountHash, ctx.Path) && (_hasSlots[i] || ctx.Path.Length < accountHash.Length * 2))
                    return true;
            }
            return false;
        }

        // Storage trie: ctx.Storage is the hashed-address whose storage root we entered. Walk only
        // the requested slots for that specific account.
        if (!_slotsByAddress.TryGetValue(ctx.Storage, out ValueHash256[]? slotHashes)) return false;
        foreach (ValueHash256 slotHash in slotHashes)
        {
            if (IsPrefix(slotHash.Bytes, ctx.Path)) return true;
        }
        return false;
    }

    public void VisitTree(in TreePathContextWithStorage ctx, in ValueHash256 rootHash) { }

    public void VisitMissingNode(in TreePathContextWithStorage ctx, in ValueHash256 nodeHash) { }

    public void VisitBranch(in TreePathContextWithStorage ctx, TrieNode node) => AddProofItem(node);

    public void VisitExtension(in TreePathContextWithStorage ctx, TrieNode node) => AddProofItem(node);

    public void VisitLeaf(in TreePathContextWithStorage ctx, TrieNode node) => AddProofItem(node);

    // VisitAccount is consumed by the eth_getProof flow to extract nonce/balance/storageRoot/codeHash
    // for the AccountProof struct; the witness path only needs the raw RLP captured by VisitLeaf, so
    // we no-op this hook.
    public void VisitAccount(in TreePathContextWithStorage ctx, TrieNode node, in AccountStruct account) { }

    private void AddProofItem(TrieNode node)
    {
        // Inline nodes have no standalone hash; their RLP is already embedded in the parent's RLP, so
        // EIP-1186 / go-ethereum convention is to omit them. Matches AccountProofCollector.
        if (node.Keccak is null) return;
        _nodes.Add(node.FullRlp.ToArray());
    }

    // currentPath (≤ 64 nibbles) must be a prefix of the 64-nibble target key; nibbles are read straight
    // from the 32-byte hash rather than from a pre-expanded Nibble[].
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
