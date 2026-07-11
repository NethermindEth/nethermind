// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Generates multiproofs by walking the persistent trie for multiple target keys simultaneously.
/// A single walk produces all intermediate hashes needed for the sparse trie to compute the root.
/// </summary>
public static class MultiProofReader
{
    public static DecodedMultiProof ReadAccountProofs(
        ITrieNodeReader reader, Hash256 stateRoot, Hash256[] hashedAddresses)
        => ReadAccountProofs(reader, stateRoot, hashedAddresses, null);

    /// <summary>
    /// Reads multi-proofs with optional per-target minLen filtering. Nodes at depth &lt; the
    /// minimum minLen across all matching targets are NOT added to the output (the caller
    /// already has them revealed). Mirrors Reth's ProofV2Target.with_min_len() optimization.
    /// Pass null minLens to fetch full proofs from root (initial proof read).
    /// </summary>
    public static DecodedMultiProof ReadAccountProofs(
        ITrieNodeReader reader, Hash256 stateRoot, Hash256[] hashedAddresses, byte[]? minLens)
    {
        DecodedMultiProof proof = new();
        if (stateRoot == Keccak.EmptyTreeHash || hashedAddresses.Length == 0)
            return proof;

        (byte[][] targets, byte[] sortedMinLens) = SortTargetsWithMinLen(hashedAddresses, minLens);
        LoadRlpFunc loadRlp = new StateLoadRlp(reader);
        WalkTrie(loadRlp, stateRoot, targets, sortedMinLens, proof.AccountNodes);
        return proof;
    }

    /// <summary>
    /// A blinded subtrie boundary identified by the sparse trie's UpdateLeaves callback.
    /// </summary>
    /// <param name="BlindedPath">Path from root to where blinding starts.</param>
    /// <param name="BlindedRlp">Stored RLP for the blinded child (32-byte hash or inline RLP).</param>
    /// <param name="TargetNibbles">Full hashed key as nibbles — its prefix equals BlindedPath.</param>
    public readonly record struct BlindedProofTarget(
        TreePath BlindedPath,
        RlpNode BlindedRlp,
        byte[] TargetNibbles);

    /// <summary>
    /// Reads proofs starting from each target's blinded subtrie boundary instead of from the
    /// state/storage root. The sparse trie already has nodes above the boundary revealed, so
    /// the proof reader does ONE DB load per blinded subtrie (zero for inline blinded RLPs)
    /// instead of walking root-to-target.
    /// <remarks>
    /// Targets sharing the same blinded subtrie are coalesced and walked once.
    /// Pass null <paramref name="accountPathHash"/> for the account trie; non-null for a
    /// specific contract's storage trie.
    /// </remarks>
    /// </summary>
    public static DecodedMultiProof ReadProofsFromBlinded(
        ITrieNodeReader reader,
        Hash256? accountPathHash,
        List<BlindedProofTarget> blindedTargets)
    {
        DecodedMultiProof proof = new();
        if (blindedTargets.Count == 0) return proof;

        List<ProofNode> output;
        if (accountPathHash is null)
        {
            output = proof.AccountNodes;
            WalkBlindedGroups(new StateLoadRlp(reader), blindedTargets, output);
        }
        else
        {
            output = [];
            WalkBlindedGroups(new StorageLoadRlp(reader, accountPathHash), blindedTargets, output);
            if (output.Count > 0) proof.StorageNodes[accountPathHash] = output;
        }
        return proof;
    }

    private static void WalkBlindedGroups<T>(T loadRlp, List<BlindedProofTarget> blindedTargets, List<ProofNode> output)
        where T : LoadRlpFunc
    {
        // Group by (blindedPath, blindedRlp) — coalesce only when BOTH match. Earlier versions
        // keyed by RLP bytes alone, which would merge two distinct blinded boundaries that
        // happened to share the same subtrie content (e.g., empty/canonical subtries, or two
        // contracts with identical storage). The walker then emitted the proof at one path
        // only, leaving the other position un-revealed and producing a wrong root.
        // Fast-path: a single blinded target is the overwhelmingly common case for
        // small-block retries (one blinded boundary on one account/slot key). Bypass the
        // grouping dictionary entirely and walk it directly.
        if (blindedTargets.Count == 1)
        {
            BlindedProofTarget only = blindedTargets[0];
            WalkSingleBlindedGroup(loadRlp, only.BlindedPath, only.BlindedRlp, [only], output);
            return;
        }

        // Group by (blindedPath, blindedRlp) â€” coalesce only when BOTH match. The previous
        // AsSpan().ToArray() copied the RLP bytes per target just to key the dictionary;
        // RlpNode's backing byte[] is already content-stable for the lifetime of this call,
        // so we reuse it directly via UnderlyingBytes.
        Dictionary<(TreePath, RlpBytesKey), List<BlindedProofTarget>> groups = [];
        foreach (BlindedProofTarget target in blindedTargets)
        {
            byte[] keyBytes = target.BlindedRlp.UnderlyingBytes ?? Array.Empty<byte>();
            (TreePath, RlpBytesKey) key = (target.BlindedPath, new RlpBytesKey(keyBytes));
            if (!groups.TryGetValue(key, out List<BlindedProofTarget>? bucket))
            {
                bucket = [];
                groups[key] = bucket;
            }
            bucket.Add(target);
        }

        foreach (KeyValuePair<(TreePath, RlpBytesKey), List<BlindedProofTarget>> grp in groups)
        {
            List<BlindedProofTarget> targets = grp.Value;
            WalkSingleBlindedGroup(loadRlp, targets[0].BlindedPath, targets[0].BlindedRlp, targets, output);
        }
    }

    private static void WalkSingleBlindedGroup<T>(
        T loadRlp,
        TreePath blindedPath,
        RlpNode blindedRlp,
        List<BlindedProofTarget> targets,
        List<ProofNode> output) where T : LoadRlpFunc
    {
        // Resolve the blinded subtrie root: load from DB if it's a hash reference,
        // decode inline RLP directly otherwise.
        byte[] rootRlpBytes;
        if (blindedRlp.IsHash())
        {
            rootRlpBytes = loadRlp.Load(blindedPath, blindedRlp.AsHash(), ReadFlags.None);
        }
        else
        {
            // Inline blinded: reuse the existing backing byte[] when available so we don't
            // copy the same RLP bytes a second time just to hand to the decoder.
            rootRlpBytes = blindedRlp.UnderlyingBytes ?? blindedRlp.AsSpan().ToArray();
        }
        ProofNode rootProof = DecodeProofNode(rootRlpBytes, blindedPath);
        output.Add(rootProof);

        int startDepth = blindedPath.Length;

        // Single-target fast path: skip the sort + minLens fan-out array allocations.
        if (targets.Count == 1)
        {
            byte[] singleTarget = targets[0].TargetNibbles;
            byte boundary = (byte)Math.Min(startDepth + 1, byte.MaxValue);
            // WalkNode reads minLens[i] inside its inner loops; we only have one target so
            // a single-element byte[] is unavoidable, but it's tiny (1 byte) compared with
            // the multi-target N-byte array allocation.
            byte[] singleMin = [boundary];
            byte[][] single = [singleTarget];
            WalkNode(loadRlp, rootProof, blindedPath, single, singleMin, 0, 1, startDepth, output);
            return;
        }

        byte[][] sortedTargets = new byte[targets.Count][];
        for (int i = 0; i < targets.Count; i++) sortedTargets[i] = targets[i].TargetNibbles;
        Array.Sort(sortedTargets, NibbleCompare);

        // All targets in this group want nodes at depth >= startDepth + 1.
        byte[] minLens = new byte[sortedTargets.Length];
        byte boundaryDepth = (byte)Math.Min(startDepth + 1, byte.MaxValue);
        for (int i = 0; i < minLens.Length; i++) minLens[i] = boundaryDepth;

        WalkNode(loadRlp, rootProof, blindedPath, sortedTargets, minLens,
            0, sortedTargets.Length, startDepth, output);
    }

    private static int NibbleCompare(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int c = a[i] - b[i];
            if (c != 0) return c;
        }
        return a.Length - b.Length;
    }

    /// <summary>
    /// Wrapper that gives byte[] structural equality for use as a Dictionary key.
    /// Used to coalesce blinded targets sharing the same RLP (hash or inline).
    /// </summary>
    private readonly struct RlpBytesKey(byte[] bytes) : IEquatable<RlpBytesKey>
    {
        private readonly byte[] _bytes = bytes;
        public bool Equals(RlpBytesKey other) => _bytes.AsSpan().SequenceEqual(other._bytes);
        public override bool Equals(object? obj) => obj is RlpBytesKey k && Equals(k);
        public override int GetHashCode()
        {
            // FNV-1a 32-bit; good enough for proof-batch coalescing dictionaries.
            uint h = 2166136261u;
            foreach (byte b in _bytes) { h ^= b; h *= 16777619u; }
            return (int)h;
        }
    }

    public static DecodedMultiProof ReadStorageProofs(
        ITrieNodeReader reader, Hash256 accountPathHash, Hash256 storageRoot, Hash256[] hashedSlots)
        => ReadStorageProofs(reader, accountPathHash, storageRoot, hashedSlots, null);

    /// <summary>
    /// Reads storage multi-proofs with optional per-target minLen filtering, matching the
    /// account proof reader. Use null minLens to fetch full root-to-leaf proofs.
    /// </summary>
    public static DecodedMultiProof ReadStorageProofs(
        ITrieNodeReader reader, Hash256 accountPathHash, Hash256 storageRoot, Hash256[] hashedSlots, byte[]? minLens)
    {
        DecodedMultiProof proof = new();
        if (storageRoot == Keccak.EmptyTreeHash || hashedSlots.Length == 0)
            return proof;

        List<ProofNode> storageNodes = [];
        (byte[][] targets, byte[] sortedMinLens) = SortTargetsWithMinLen(hashedSlots, minLens);
        LoadRlpFunc loadRlp = new StorageLoadRlp(reader, accountPathHash);
        WalkTrie(loadRlp, storageRoot, targets, sortedMinLens, storageNodes);

        if (storageNodes.Count > 0)
            proof.StorageNodes[accountPathHash] = storageNodes;
        return proof;
    }

    private interface LoadRlpFunc
    {
        byte[] Load(in TreePath path, Hash256 hash, ReadFlags flags);
    }

    private readonly struct StateLoadRlp(ITrieNodeReader reader) : LoadRlpFunc
    {
        public byte[] Load(in TreePath path, Hash256 hash, ReadFlags flags) =>
            reader.LoadStateRlp(path, hash, flags);
    }

    private readonly struct StorageLoadRlp(ITrieNodeReader reader, Hash256 accountPathHash) : LoadRlpFunc
    {
        public byte[] Load(in TreePath path, Hash256 hash, ReadFlags flags) =>
            reader.LoadStorageRlp(accountPathHash, path, hash, flags);
    }

    private static void WalkTrie<T>(
        T loadRlp,
        Hash256 rootHash,
        byte[][] sortedTargetNibbles,
        byte[] sortedMinLens,
        List<ProofNode> output) where T : LoadRlpFunc
    {
        byte[] rootRlp = loadRlp.Load(TreePath.Empty, rootHash, ReadFlags.None);
        ProofNode rootProof = DecodeProofNode(rootRlp, TreePath.Empty);
        // Add root only if at least one target needs depth 0 (i.e., minLen == 0 for some target).
        if (AnyTargetNeedsDepth(sortedMinLens, 0, sortedMinLens.Length, 0))
            output.Add(rootProof);

        WalkNode(loadRlp, rootProof, TreePath.Empty, sortedTargetNibbles, sortedMinLens, 0, sortedTargetNibbles.Length, 0, output);
    }

    private static bool AnyTargetNeedsDepth(byte[] minLens, int start, int end, int depth)
    {
        for (int i = start; i < end; i++)
            if (minLens[i] <= depth) return true;
        return false;
    }

    private static void WalkNode<T>(
        T loadRlp,
        ProofNode node,
        TreePath currentPath,
        byte[][] targets,
        byte[] minLens,
        int targetStart,
        int targetEnd,
        int nibbleDepth,
        List<ProofNode> output) where T : LoadRlpFunc
    {
        if (targetStart >= targetEnd) return;

        switch (node.Kind)
        {
            case ProofNodeKind.Leaf:
                return;

            case ProofNodeKind.Extension:
                {
                    byte[] extKey = node.Key ?? [];
                    int newDepth = nibbleDepth + extKey.Length;

                    // Find targets that match the extension key (for further descent)
                    int matchStart = targetEnd;
                    int matchEnd = targetStart;
                    for (int i = targetStart; i < targetEnd; i++)
                    {
                        if (MatchesPrefix(targets[i], nibbleDepth, extKey))
                        {
                            if (i < matchStart) matchStart = i;
                            matchEnd = i + 1;
                        }
                    }

                    // Always load the underlying Branch and include in the proof — even when no target
                    // fully matches the extension prefix. The sparse trie needs the inner Branch's content
                    // to handle absence-at-extension cases (where an update partially matches the prefix
                    // then diverges, requiring a split that uses the inner Branch's children).
                    if (node.ChildRlps is { Length: > 0 } && !node.ChildRlps[0].IsNull)
                    {
                        RlpNode childRef = node.ChildRlps[0];
                        TreePath childPath = currentPath.Append(extKey);
                        // Only emit this child if some target needs its depth (minLen <= newDepth)
                        bool needEmit = AnyTargetNeedsDepth(minLens, matchStart < matchEnd ? matchStart : targetStart, matchStart < matchEnd ? matchEnd : targetEnd, newDepth);

                        if (childRef.IsHash())
                        {
                            Hash256 childHash = childRef.AsHash();
                            byte[] childRlp = loadRlp.Load(childPath, childHash, ReadFlags.None);
                            ProofNode childProof = DecodeProofNode(childRlp, childPath);
                            if (needEmit) output.Add(childProof);
                            if (matchStart < matchEnd)
                                WalkNode(loadRlp, childProof, childPath, targets, minLens, matchStart, matchEnd, newDepth, output);
                        }
                        else
                        {
                            ProofNode childProof = DecodeProofNode(childRef.AsSpan().ToArray(), childPath);
                            if (needEmit) output.Add(childProof);
                            if (matchStart < matchEnd)
                                WalkNode(loadRlp, childProof, childPath, targets, minLens, matchStart, matchEnd, newDepth, output);
                        }
                    }
                    break;
                }

            case ProofNodeKind.Branch:
                {
                    // Targets are sorted by nibble path, so for any branch at depth N we can
                    // partition them by their depth-N nibble in a single linear pass instead of
                    // 16 full rescans (the old code was O(16 * range_size) per branch). Targets
                    // shorter than nibbleDepth+1 don't have a nibble at this depth and are skipped.
                    int i = targetStart;
                    int childDepth = nibbleDepth + 1;
                    int childCount = node.ChildMask.CountBits();
                    if (childCount == 0) break;

                    while (i < targetEnd)
                    {
                        byte[] t = targets[i];
                        if (nibbleDepth >= t.Length) { i++; continue; }
                        int nibble = t[nibbleDepth];
                        int subStart = i;
                        do { i++; } while (i < targetEnd
                            && nibbleDepth < targets[i].Length
                            && targets[i][nibbleDepth] == nibble);
                        int subEnd = i;

                        if (!node.ChildMask.IsBitSet(nibble)) continue;
                        RlpNode childRef = node.ChildRlps is not null && nibble < node.ChildRlps.Length
                            ? node.ChildRlps[nibble]
                            : default;
                        if (childRef.IsNull) continue;

                        TreePath childPath = currentPath.Append(nibble);
                        bool needEmit = AnyTargetNeedsDepth(minLens, subStart, subEnd, childDepth);

                        byte[] childRlp = childRef.IsHash()
                            ? loadRlp.Load(childPath, childRef.AsHash(), ReadFlags.None)
                            : childRef.AsSpan().ToArray();
                        ProofNode childProof = DecodeProofNode(childRlp, childPath);
                        if (needEmit) output.Add(childProof);
                        WalkNode(loadRlp, childProof, childPath, targets, minLens, subStart, subEnd, childDepth, output);
                    }
                    break;
                }
        }
    }

    public static ProofNode DecodeProofNode(byte[] rlp, TreePath path)
    {
        RlpReader ctx = new(rlp);
        ctx.ReadSequenceLength();

        int startPos = ctx.Position;
        int itemCount = ctx.PeekNumberOfItemsRemaining(null, 18);
        ctx.Position = startPos;

        if (itemCount >= 17)
            return DecodeBranchProof(rlp, ref ctx, path);
        if (itemCount == 2)
            return DecodeTwoItemProof(rlp, ref ctx, path);

        return new ProofNode { Path = path, Kind = ProofNodeKind.Empty, RawRlp = rlp };
    }

    private static ProofNode DecodeBranchProof(byte[] rlp, ref RlpReader ctx, TreePath path)
    {
        TrieMask childMask = TrieMask.Empty;
        RlpNode[] childRlps = new RlpNode[16];

        for (int i = 0; i < 16; i++)
        {
            int itemStart = ctx.Position;
            int itemLen = ctx.PeekNextRlpLength();

            if (itemLen == 1 && rlp[itemStart] == 0x80)
            {
                ctx.SkipItem();
                continue;
            }

            childMask = childMask.SetBit(i);

            if (itemLen == 33 && rlp[itemStart] == 0xa0)
            {
                childRlps[i] = RlpNode.FromHashSpan(rlp.AsSpan(itemStart + 1, 32));
            }
            else
            {
                byte[] childBytes = new byte[itemLen];
                rlp.AsSpan(itemStart, itemLen).CopyTo(childBytes);
                childRlps[i] = RlpNode.FromRlp(childBytes);
            }
            ctx.Position = itemStart + itemLen;
        }
        ctx.SkipItem(); // 17th element (branch value, always 0x80)

        return new ProofNode
        {
            Path = path,
            Kind = ProofNodeKind.Branch,
            ChildMask = childMask,
            ChildRlps = childRlps,
            RawRlp = rlp,
        };
    }

    private static ProofNode DecodeTwoItemProof(byte[] rlp, ref RlpReader ctx, TreePath path)
    {
        ReadOnlySpan<byte> encodedKeySpan = ctx.DecodeByteArraySpan();
        byte[] encodedKey = encodedKeySpan.ToArray();
        (byte[] nibbleKey, bool isLeaf) = HexPrefix.FromBytes(encodedKey);

        if (isLeaf)
        {
            ReadOnlySpan<byte> valueSpan = ctx.DecodeByteArraySpan();
            byte[] value = valueSpan.ToArray();
            return new ProofNode
            {
                Path = path,
                Kind = ProofNodeKind.Leaf,
                Key = nibbleKey,
                Value = value,
                RawRlp = rlp,
            };
        }

        // Extension
        int childStart = ctx.Position;
        int childLen = ctx.PeekNextRlpLength();

        RlpNode childRlp;
        if (childLen == 33 && rlp[childStart] == 0xa0)
        {
            childRlp = RlpNode.FromHash(new Hash256(rlp.AsSpan(childStart + 1, 32)));
        }
        else
        {
            byte[] childBytes = new byte[childLen];
            rlp.AsSpan(childStart, childLen).CopyTo(childBytes);
            childRlp = RlpNode.FromRlp(childBytes);
        }

        return new ProofNode
        {
            Path = path,
            Kind = ProofNodeKind.Extension,
            Key = nibbleKey,
            ChildRlps = [childRlp],
            ChildNibble = -1,
            RawRlp = rlp,
        };
    }

    private static byte[][] SortTargets(Hash256[] keys)
    {
        byte[][] nibbles = new byte[keys.Length][];
        for (int i = 0; i < keys.Length; i++)
            nibbles[i] = Nibbles.BytesToNibbleBytes(keys[i].Bytes);
        Array.Sort(nibbles, CompareNibbleArrays);
        return nibbles;
    }

    /// <summary>Sorts targets (and aligned minLens). Pass null minLens to default everything to 0.</summary>
    private static (byte[][] targets, byte[] minLens) SortTargetsWithMinLen(Hash256[] keys, byte[]? minLens)
    {
        int n = keys.Length;
        byte[][] nibbles = new byte[n][];
        byte[] sortedMinLens = new byte[n];
        int[] indices = new int[n];
        for (int i = 0; i < n; i++)
        {
            nibbles[i] = Nibbles.BytesToNibbleBytes(keys[i].Bytes);
            indices[i] = i;
        }
        // Sort indices by nibble order
        Array.Sort(indices, (a, b) => CompareNibbleArrays(nibbles[a], nibbles[b]));
        byte[][] sortedNibbles = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            sortedNibbles[i] = nibbles[indices[i]];
            sortedMinLens[i] = minLens is null ? (byte)0 : minLens[indices[i]];
        }
        return (sortedNibbles, sortedMinLens);
    }

    private static int CompareNibbleArrays(byte[] a, byte[] b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0) return cmp;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static bool MatchesPrefix(byte[] target, int offset, byte[] prefix)
    {
        if (offset + prefix.Length > target.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (target[offset + i] != prefix[i]) return false;
        }
        return true;
    }
}
