// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.Sparse;

/// <summary>A batched, hash-validated parent-node read: RLP for <c>(path, expected hash)</c>.</summary>
internal readonly struct SparseNodeRequest(in TreePath path, in ValueHash256 hash)
{
    public readonly TreePath Path = path;
    public readonly ValueHash256 Hash = hash;
}

/// <summary>
/// Resolves committed parent trie nodes for a sparse trie. One instance is bound to one trie
/// (for storage tries, to one account).
/// </summary>
internal interface ISparseTrieNodeSource
{
    /// <summary>
    /// Resolves each request to its node RLP; <see cref="CappedArray{T}.Null"/> marks a missing
    /// node. Implementations must validate that the returned bytes hash to the requested hash
    /// (throwing on mismatch) before exposing them, and must preserve request/result association.
    /// </summary>
    void Resolve(ReadOnlySpan<SparseNodeRequest> requests, Span<CappedArray<byte>> results);
}

/// <summary>One final update: an empty/null <paramref name="value"/> deletes the key.</summary>
internal readonly struct SparseTrieUpdate(in ValueHash256 key, byte[]? value)
{
    public readonly ValueHash256 Key = key;
    public readonly byte[]? Value = value;

    public bool IsDelete => Value is null || Value.Length == 0;
}

/// <summary>A persistable node staged during root calculation, awaiting publication.</summary>
internal readonly record struct SparseTrieStagedNode(TreePath Path, ValueHash256 Hash, byte[] Rlp);

/// <summary>
/// A sparse, proof-backed Merkle Patricia trie: reveals only the committed parent nodes a batch
/// of updates touches, applies canonical mutations, and re-encodes/hashes only dirty paths,
/// writing each persistable RLP once into its final owned array.
/// </summary>
/// <remarks>
/// Node encoding, hex-prefix compact paths, and trie shape follow the Yellow Paper Appendix D
/// (modified Merkle Patricia trie) with Appendix B RLP; children whose RLP is shorter than
/// 32 bytes are embedded in the parent, and fixed-length keys leave the seventeenth branch item
/// empty. Keys are fixed 32 bytes (64 nibbles). One instance has one mutable owner; nothing here
/// is thread-safe. Reveal is a synchronous multi-round loop: sorted target ranges descend as far as
/// materialized nodes allow, missing hashed boundaries are collected and resolved in one batch
/// per round, and only the affected ranges resume. A round that leaves requests unresolved
/// throws, so the loop cannot stall. Update batches must contain unique keys with final values
/// (duplicates throw). Deletions may require revealing a surviving sibling to collapse a branch
/// canonically; those reveals are batched per delete round. Persistable RLP of at least 32 bytes
/// is encoded directly into a <see cref="SparseTrieStagedNode"/>-owned array and staged; shorter
/// RLP is embedded in its parent. Staged nodes form a connected region under the root (every
/// ancestor of a staged node is staged), so <see cref="DrainUnpublished"/> walks only flagged
/// subtrees and never scans the whole arena; records orphaned by later mutation or deletion are
/// dropped without publication.
/// </remarks>
internal sealed class SparseTrie(ISparseTrieNodeSource source, ValueHash256 anchorRoot, int nodeCapacityHint = 0) : IDisposable
{
    private const int MaxDepth = 64;
    private const byte ExtensionChildNibble = 0xFF;
    private const int MaxLeafValueLength = byte.MaxValue;

    private readonly ISparseTrieNodeSource _source = source;
    private readonly SparseTrieArena _arena = new(nodeCapacityHint);
    private ArrayPoolList<SparseTrieStagedNode>? _staged;
    private int _rootNode = -1;
    private ValueHash256 _rootHash = anchorRoot;
    private bool _rootDirty;

    /// <summary>The anchor root before the first calculation; the calculated root afterwards.</summary>
    public ValueHash256 RootHash => _rootHash;

    /// <summary>True when updates were applied after the last calculation.</summary>
    public bool IsDirty => _rootDirty;

    public long RentedBytes => _arena.RentedBytes;
    public long DeadBytes => _arena.DeadBytes;
    public int NodeCount => _arena.NodeCount;

    /// <summary>
    /// Reveals the parents the batch touches and applies it: inserts and value updates first,
    /// then deletions with canonical collapse. Consumes <paramref name="updates"/>: the span is
    /// sorted and then overwritten in place (deletes compacted to the front), so callers must
    /// not reuse its contents.
    /// </summary>
    public void Apply(Span<SparseTrieUpdate> updates)
    {
        if (updates.IsEmpty)
        {
            return;
        }

        updates.Sort(default(UpdateKeyComparer));
        int deleteCount = 0;
        for (int i = 0; i < updates.Length; i++)
        {
            if (i > 0 && updates[i].Key == updates[i - 1].Key)
            {
                throw new ArgumentException($"Duplicate update key {updates[i].Key}; callers must reduce duplicates to final values", nameof(updates));
            }

            if (updates[i].IsDelete)
            {
                deleteCount++;
            }
        }

        Reveal(updates);
        _rootDirty = true;

        if (deleteCount < updates.Length)
        {
            TreePath path = TreePath.Empty;
            (int newRoot, _) = ApplyWrites(_rootNode, ref path, updates, 0, updates.Length);
            _rootNode = newRoot;
        }

        if (deleteCount > 0)
        {
            if (deleteCount < updates.Length)
            {
                // The writes are already applied and consumed; stably compacting the sorted
                // deletes to the front keeps the delete walk from rescanning write entries
                // that share prefixes with delete targets.
                int packed = 0;
                for (int i = 0; i < updates.Length; i++)
                {
                    if (updates[i].IsDelete)
                    {
                        updates[packed++] = updates[i];
                    }
                }
            }

            ApplyDeletes(updates[..deleteCount]);
        }

        if (_rootNode < 0)
        {
            _rootHash = ValueKeccak.EmptyTreeHash;
        }
    }

    /// <summary>
    /// Re-encodes and hashes every dirty path post-order, staging persistable nodes, and returns
    /// the root hash. Idempotent until the next <see cref="Apply"/>.
    /// </summary>
    public ValueHash256 CalculateRoot()
    {
        if (!_rootDirty)
        {
            return _rootHash;
        }

        if (_rootNode >= 0 && _arena.Node(_rootNode).IsDirty)
        {
            TreePath path = TreePath.Empty;
            EncodeNode(_rootNode, ref path, isRoot: true);
            _rootHash = _arena.Node(_rootNode).Hash;
        }

        _rootDirty = false;
        return _rootHash;
    }

    /// <summary>
    /// Walks the unpublished frontier from the root, moving every reachable staged node into
    /// <paramref name="destination"/> and clearing the staging state, including records orphaned
    /// by intermediate roots. Must not be called while <see cref="IsDirty"/>.
    /// </summary>
    public void DrainUnpublished(ArrayPoolList<SparseTrieStagedNode> destination)
    {
        if (_rootDirty)
        {
            throw new InvalidOperationException("Cannot publish while updates are pending calculation");
        }

        if (_staged is null)
        {
            return;
        }

        if (_rootNode >= 0)
        {
            CollectUnpublished(_rootNode, destination);
        }

        _staged.Dispose();
        _staged = null;
    }

    private void CollectUnpublished(int nodeIndex, ArrayPoolList<SparseTrieStagedNode> destination)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        if (!node.IsUnpublished)
        {
            return;
        }

        destination.Add(_staged![node.StagedRecord]);
        node.Flags &= ~SparseNodeFlags.Unpublished;
        node.StagedRecord = -1;

        if (node.Kind == SparseNodeKind.Extension)
        {
            if (node.ChildSlice >= 0)
            {
                CollectUnpublished(node.ChildSlice, destination);
            }
        }
        else if (node.Kind == SparseNodeKind.Branch)
        {
            Span<int> children = _arena.ChildSlice(node.ChildSlice, node.ChildCount);
            foreach (int entry in children)
            {
                if (entry >= 0)
                {
                    CollectUnpublished(entry, destination);
                }
            }
        }
    }

    public void Dispose()
    {
        _staged?.Dispose();
        _staged = null;
        _arena.Dispose();
    }

    private readonly struct UpdateKeyComparer : IComparer<SparseTrieUpdate>
    {
        public int Compare(SparseTrieUpdate x, SparseTrieUpdate y) => x.Key.CompareTo(y.Key);
    }

    private static int KeyNibble(in ValueHash256 key, int index)
    {
        int b = key.Bytes[index >> 1];
        return (index & 1) == 0 ? b >> 4 : b & 0xF;
    }

    private static void ThrowTrie(string message) => throw new TrieException(message);

    /// <summary>
    /// Rejects a node whose prefix cannot be valid at <paramref name="depth"/>: leaves must
    /// terminate exactly at 64 nibbles, extensions must leave room for a subtree. This guards
    /// every materialization site, including reveals that no target range walks afterwards.
    /// </summary>
    private void ValidateNodeDepth(int nodeIndex, int depth)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        if (node.Kind == SparseNodeKind.Leaf)
        {
            if (depth + node.PrefixLength != MaxDepth)
            {
                ThrowTrie($"Leaf at depth {depth} with a {node.PrefixLength}-nibble prefix does not terminate at 64 nibbles");
            }
        }
        else if (node.Kind == SparseNodeKind.Extension && depth + node.PrefixLength >= MaxDepth)
        {
            ThrowTrie($"Extension at depth {depth} with a {node.PrefixLength}-nibble prefix leaves no room for its subtree");
        }
        else if (node.Kind == SparseNodeKind.Branch && depth >= MaxDepth)
        {
            ThrowTrie($"Branch below the maximum trie depth at depth {depth}");
        }
    }

    // ---------------- Reveal ----------------

    private struct PendingReveal
    {
        public TreePath Path;
        public ValueHash256 Hash;
        public int Parent;       // -1 = the root itself
        public byte ChildNibble; // ExtensionChildNibble when the parent is an extension
        public int Start;
        public int End;          // End == Start marks a reveal with no target range to resume
    }

    private void Reveal(Span<SparseTrieUpdate> updates)
    {
        using ArrayPoolList<PendingReveal> pending = new(16);
        using ArrayPoolList<PendingReveal> next = new(16);

        if (_rootNode < 0)
        {
            if (_rootHash == ValueKeccak.EmptyTreeHash)
            {
                return;
            }

            pending.Add(new PendingReveal { Path = TreePath.Empty, Hash = _rootHash, Parent = -1, Start = 0, End = updates.Length });
        }
        else
        {
            TreePath path = TreePath.Empty;
            WalkReveal(_rootNode, ref path, updates, 0, updates.Length, pending);
        }

        ArrayPoolList<PendingReveal> current = pending;
        ArrayPoolList<PendingReveal> spare = next;
        while (current.Count > 0)
        {
            ResolvePending(current);

            spare.Clear();
            for (int i = 0; i < current.Count; i++)
            {
                PendingReveal item = current[i];
                int nodeIndex = item.Parent < 0 ? _rootNode : GetChildNode(item.Parent, item.ChildNibble);
                if (item.End > item.Start)
                {
                    TreePath path = item.Path;
                    WalkReveal(nodeIndex, ref path, updates, item.Start, item.End, spare);
                }
            }

            (current, spare) = (spare, current);
        }
    }

    /// <summary>Resolves one batch of reveals and materializes each result, patching parents.</summary>
    private void ResolvePending(ArrayPoolList<PendingReveal> pending)
    {
        int count = pending.Count;
        SparseNodeRequest[] requests = ArrayPool<SparseNodeRequest>.Shared.Rent(count);
        CappedArray<byte>[] results = ArrayPool<CappedArray<byte>>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                requests[i] = new SparseNodeRequest(pending[i].Path, pending[i].Hash);
            }

            _source.Resolve(requests.AsSpan(0, count), results.AsSpan(0, count));

            for (int i = 0; i < count; i++)
            {
                PendingReveal item = pending[i];
                CappedArray<byte> rlp = results[i];
                if (rlp.IsNull)
                {
                    throw new MissingTrieNodeException($"Missing sparse trie node hash={item.Hash} path={item.Path}", null, item.Path, item.Hash.ToCommitment());
                }

                int handle = _arena.AllocBytes(rlp.Length);
                rlp.AsSpan().CopyTo(_arena.Bytes(handle, rlp.Length));
                int nodeIndex = DecodeNode(handle, rlp.Length, inline: false);
                ValidateNodeDepth(nodeIndex, item.Path.Length);
                _arena.Node(nodeIndex).Hash = item.Hash;

                if (item.Parent < 0)
                {
                    _rootNode = nodeIndex;
                }
                else
                {
                    // A blinded placeholder created by an earlier structural mutation is
                    // replaced by the revealed node and freed.
                    int oldEntry = GetChildNode(item.Parent, item.ChildNibble);
                    if (oldEntry >= 0 && _arena.Node(oldEntry).Kind == SparseNodeKind.Blinded)
                    {
                        FreeNode(oldEntry);
                    }

                    SetChildNode(item.Parent, item.ChildNibble, nodeIndex);
                }
            }
        }
        finally
        {
            ArrayPool<SparseNodeRequest>.Shared.Return(requests);
            ArrayPool<CappedArray<byte>>.Shared.Return(results, clearArray: true);
        }
    }

    private int GetChildNode(int parent, byte childNibble)
    {
        ref SparseNode node = ref _arena.Node(parent);
        if (childNibble == ExtensionChildNibble)
        {
            return node.ChildSlice;
        }

        return _arena.ChildSlice(node.ChildSlice, node.ChildCount)[node.ChildSlot(childNibble)];
    }

    private void SetChildNode(int parent, byte childNibble, int childIndex)
    {
        ref SparseNode node = ref _arena.Node(parent);
        if (childNibble == ExtensionChildNibble)
        {
            node.ChildSlice = childIndex;
        }
        else
        {
            _arena.ChildSlice(node.ChildSlice, node.ChildCount)[node.ChildSlot(childNibble)] = childIndex;
        }
    }

    private void WalkReveal(int nodeIndex, ref TreePath path, ReadOnlySpan<SparseTrieUpdate> updates, int start, int end, ArrayPoolList<PendingReveal> pending)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        switch (node.Kind)
        {
            case SparseNodeKind.Leaf:
                if (path.Length + node.PrefixLength != MaxDepth)
                {
                    ThrowTrie($"Leaf at depth {path.Length} with a {node.PrefixLength}-nibble prefix does not terminate at 64 nibbles");
                }

                return;

            case SparseNodeKind.Extension:
                {
                    if (path.Length + node.PrefixLength >= MaxDepth)
                    {
                        ThrowTrie($"Extension at depth {path.Length} with a {node.PrefixLength}-nibble prefix leaves no room for its subtree");
                    }

                    Span<byte> prefix = _arena.Bytes(node.PrefixOffset, node.PrefixLength);
                    (int matchStart, int matchEnd) = FullPrefixMatchRange(updates, start, end, path.Length, prefix);
                    if (matchStart >= matchEnd)
                    {
                        return;
                    }

                    int entry = node.ChildSlice;
                    int prefixLength = node.PrefixLength;
                    if (entry < 0)
                    {
                        int materialized = TryMaterializeInline(entry, out ValueHash256 childHash);
                        if (materialized < 0)
                        {
                            TreePath childPath = path;
                            AppendNibbles(ref childPath, prefix);
                            pending.Add(new PendingReveal { Path = childPath, Hash = childHash, Parent = nodeIndex, ChildNibble = ExtensionChildNibble, Start = matchStart, End = matchEnd });
                            return;
                        }

                        node.ChildSlice = entry = materialized;
                    }
                    else if (_arena.Node(entry).Kind == SparseNodeKind.Blinded)
                    {
                        TreePath childPath = path;
                        AppendNibbles(ref childPath, prefix);
                        pending.Add(new PendingReveal { Path = childPath, Hash = _arena.Node(entry).Hash, Parent = nodeIndex, ChildNibble = ExtensionChildNibble, Start = matchStart, End = matchEnd });
                        return;
                    }

                    AppendNibbles(ref path, prefix);
                    WalkReveal(entry, ref path, updates, matchStart, matchEnd, pending);
                    path.TruncateMut(path.Length - prefixLength);
                    return;
                }

            case SparseNodeKind.Branch:
                {
                    int depth = path.Length;
                    if (depth >= MaxDepth)
                    {
                        ThrowTrie($"Branch below the maximum trie depth at {path}");
                    }

                    int i = start;
                    while (i < end)
                    {
                        int nibble = KeyNibble(updates[i].Key, depth);
                        int groupEnd = i + 1;
                        while (groupEnd < end && KeyNibble(updates[groupEnd].Key, depth) == nibble)
                        {
                            groupEnd++;
                        }

                        if ((node.OccupiedMask & (1 << nibble)) != 0)
                        {
                            int entry = _arena.ChildSlice(node.ChildSlice, node.ChildCount)[node.ChildSlot(nibble)];
                            if (entry < 0)
                            {
                                int materialized = TryMaterializeInline(entry, out ValueHash256 childHash);
                                if (materialized >= 0)
                                {
                                    SetChildNode(nodeIndex, (byte)nibble, materialized);
                                    entry = materialized;
                                }
                                else
                                {
                                    TreePath childPath = path;
                                    childPath.AppendMut(nibble);
                                    pending.Add(new PendingReveal { Path = childPath, Hash = childHash, Parent = nodeIndex, ChildNibble = (byte)nibble, Start = i, End = groupEnd });
                                    i = groupEnd;
                                    continue;
                                }
                            }
                            else if (_arena.Node(entry).Kind == SparseNodeKind.Blinded)
                            {
                                TreePath childPath = path;
                                childPath.AppendMut(nibble);
                                pending.Add(new PendingReveal { Path = childPath, Hash = _arena.Node(entry).Hash, Parent = nodeIndex, ChildNibble = (byte)nibble, Start = i, End = groupEnd });
                                i = groupEnd;
                                continue;
                            }

                            path.AppendMut(nibble);
                            WalkReveal(entry, ref path, updates, i, groupEnd, pending);
                            path.TruncateMut(path.Length - 1);
                        }

                        i = groupEnd;
                    }

                    return;
                }

            default:
                ThrowTrie($"Cannot traverse {node.Kind} sparse node at {path}");
                return;
        }
    }

    /// <summary>Range of updates whose keys fully match the prefix nibbles at <paramref name="depth"/>.</summary>
    private static (int Start, int End) FullPrefixMatchRange(ReadOnlySpan<SparseTrieUpdate> updates, int start, int end, int depth, ReadOnlySpan<byte> prefix)
    {
        if (depth + prefix.Length > MaxDepth)
        {
            ThrowTrie("Trie path exceeds 64 nibbles");
        }

        int matchStart = start;
        while (matchStart < end && !MatchesPrefix(updates[matchStart].Key, depth, prefix))
        {
            matchStart++;
        }

        int matchEnd = matchStart;
        while (matchEnd < end && MatchesPrefix(updates[matchEnd].Key, depth, prefix))
        {
            matchEnd++;
        }

        return (matchStart, matchEnd);
    }

    private static bool MatchesPrefix(in ValueHash256 key, int depth, ReadOnlySpan<byte> prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
        {
            if (KeyNibble(key, depth + i) != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendNibbles(ref TreePath path, ReadOnlySpan<byte> nibbles) => path.AppendMut(nibbles);

    /// <summary>
    /// Materializes an inline child from its owner's original RLP, or returns -1 with the child
    /// hash for a hashed reference that requires a reveal.
    /// </summary>
    private int TryMaterializeInline(int entry, out ValueHash256 hash)
    {
        int offset = ~entry;
        byte first = _arena.Bytes(offset, 1)[0];
        if (first == 0xA0)
        {
            hash = new ValueHash256(_arena.Bytes(offset + 1, 32));
            return -1;
        }

        // An embedded child list must be under 32 bytes in total (0xC0 + 30 at most); larger
        // children are hashed in a canonical trie.
        if (first is >= 0xC0 and <= 0xDE)
        {
            hash = default;
            return DecodeNode(offset, first - 0xC0 + 1, inline: true);
        }

        ThrowTrie($"Malformed child reference 0x{first:x2} in sparse node RLP");
        hash = default;
        return -1;
    }

    // ---------------- Decode ----------------

    /// <summary>Decodes one node RLP already resident in the byte segment, in a single pass.</summary>
    private int DecodeNode(int rlpOffset, int rlpLength, bool inline)
    {
        Span<byte> rlp = _arena.Bytes(rlpOffset, rlpLength);
        byte first = rlp[0];
        int position;
        int contentLength;
        if (first < 0xC0)
        {
            ThrowTrie($"Sparse node RLP is not a list (0x{first:x2})");
            return -1;
        }

        if (first < 0xF8)
        {
            contentLength = first - 0xC0;
            position = 1;
        }
        else
        {
            int lengthOfLength = first - 0xF7;
            if (lengthOfLength > 2 || 1 + lengthOfLength > rlpLength)
            {
                ThrowTrie("Malformed sparse node RLP length");
            }

            contentLength = rlp[1];
            if (lengthOfLength == 2)
            {
                if (contentLength == 0)
                {
                    ThrowTrie("Non-canonical sparse node RLP length with a leading zero");
                }

                contentLength = (contentLength << 8) | rlp[2];
            }

            if (contentLength < 56)
            {
                ThrowTrie("Non-canonical sparse node RLP length");
            }

            position = 1 + lengthOfLength;
        }

        if (position + contentLength != rlpLength)
        {
            ThrowTrie($"Malformed sparse node RLP: sequence length {contentLength} does not fill {rlpLength} bytes");
        }

        int nodeIndex = _arena.AllocNode();
        ref SparseNode node = ref _arena.Node(nodeIndex);
        node.RlpOffset = rlpOffset;
        node.RlpLength = (ushort)rlpLength;
        // An inline node's RLP aliases its parent's original RLP; a revealed node owns its copy.
        node.Flags |= inline ? SparseNodeFlags.Inline : SparseNodeFlags.OwnedRlp;

        // Walk the items once, recording where each starts; 17 items make a branch, 2 a leaf or
        // extension. Item bounds are validated by the walk itself.
        Span<int> itemOffsets = stackalloc int[17];
        int count = 0;
        while (position < rlpLength)
        {
            if (count == 17)
            {
                ThrowTrie("Sparse node RLP with more than 17 items");
            }

            itemOffsets[count++] = position;
            position += ItemLength(rlp, position, rlpLength);
        }

        if (count == 17)
        {
            DecodeBranch(ref node, rlp, itemOffsets, rlpOffset);
        }
        else if (count == 2)
        {
            DecodeLeafOrExtension(ref node, rlp, itemOffsets[0], itemOffsets[1], rlpOffset);
        }
        else
        {
            ThrowTrie($"Malformed sparse node RLP with {count} items");
        }

        return nodeIndex;
    }

    /// <summary>Total encoded length (header plus content) of the item at <paramref name="position"/>.</summary>
    private static int ItemLength(ReadOnlySpan<byte> rlp, int position, int rlpLength)
    {
        byte first = rlp[position];
        int length = first switch
        {
            < 0x80 => 1,
            < 0xB8 => 1 + first - 0x80,
            0xB8 => position + 1 < rlpLength ? 2 + rlp[position + 1] : int.MaxValue,
            < 0xC0 => int.MaxValue, // longer strings cannot occur inside a trie node
            < 0xF8 => 1 + first - 0xC0,
            _ => int.MaxValue, // long lists cannot occur inside a trie node
        };

        // Overflow-free bound: position < rlpLength here, and length may be the sentinel.
        if (length > rlpLength - position)
        {
            ThrowTrie($"Malformed sparse node RLP item 0x{first:x2}");
        }

        return length;
    }

    private void DecodeBranch(ref SparseNode node, ReadOnlySpan<byte> rlp, ReadOnlySpan<int> itemOffsets, int rlpOffset)
    {
        node.Kind = SparseNodeKind.Branch;

        int occupied = 0;
        for (int nibble = 0; nibble < 16; nibble++)
        {
            byte first = rlp[itemOffsets[nibble]];
            if (first == 0x80)
            {
                continue;
            }

            // A child is a 32-byte hash or an embedded list strictly shorter than 32 bytes.
            if (first == 0xA0 || first is >= 0xC0 and <= 0xDE)
            {
                occupied |= 1 << nibble;
            }
            else
            {
                ThrowTrie($"Malformed branch child item 0x{first:x2}");
            }
        }

        if (rlp[itemOffsets[16]] != 0x80)
        {
            ThrowTrie("Branch value must be empty for fixed-length-key tries");
        }

        if (occupied == 0)
        {
            ThrowTrie("Branch without children");
        }

        node.OccupiedMask = (ushort)occupied;
        int childCount = BitOperations.PopCount((uint)occupied);
        node.ChildSlice = _arena.AllocChildSlice(childCount);
        Span<int> children = _arena.ChildSlice(node.ChildSlice, childCount);
        int slot = 0;
        for (int nibble = 0; nibble < 16; nibble++)
        {
            if ((occupied & (1 << nibble)) != 0)
            {
                children[slot++] = ~(rlpOffset + itemOffsets[nibble]);
            }
        }
    }

    private void DecodeLeafOrExtension(ref SparseNode node, ReadOnlySpan<byte> rlp, int keyItem, int secondItem, int rlpOffset)
    {
        (int keyStart, int keyLength) = StringContent(rlp, keyItem);
        if (keyLength == 0)
        {
            ThrowTrie("Empty hex-prefix key in sparse node RLP");
        }

        ReadOnlySpan<byte> hexPrefix = rlp.Slice(keyStart, keyLength);
        bool isLeaf = (hexPrefix[0] & 0x20) != 0;
        bool oddLength = (hexPrefix[0] & 0x10) != 0;
        if ((hexPrefix[0] & 0xC0) != 0 || (!oddLength && (hexPrefix[0] & 0x0F) != 0))
        {
            ThrowTrie($"Malformed hex-prefix flag byte 0x{hexPrefix[0]:x2} in sparse node RLP");
        }

        int nibbleCount = (hexPrefix.Length - 1) * 2 + (oddLength ? 1 : 0);
        if (nibbleCount > MaxDepth)
        {
            ThrowTrie($"Node prefix of {nibbleCount} nibbles exceeds the 64-nibble key length");
        }

        node.PrefixLength = (byte)nibbleCount;
        if (nibbleCount > 0)
        {
            node.PrefixOffset = _arena.AllocBytes(nibbleCount);
            Span<byte> nibbles = _arena.Bytes(node.PrefixOffset, nibbleCount);
            int n = 0;
            if (oddLength)
            {
                nibbles[n++] = (byte)(hexPrefix[0] & 0xF);
            }

            for (int i = 1; i < hexPrefix.Length; i++)
            {
                nibbles[n++] = (byte)(hexPrefix[i] >> 4);
                nibbles[n++] = (byte)(hexPrefix[i] & 0xF);
            }
        }

        if (isLeaf)
        {
            node.Kind = SparseNodeKind.Leaf;
            (int valueStart, int valueLength) = StringContent(rlp, secondItem);
            if (valueLength == 0 || valueLength > MaxLeafValueLength)
            {
                ThrowTrie($"Unsupported leaf value length {valueLength} in sparse node RLP");
            }

            // The value points into the original RLP (offset past the item's own header).
            node.ValueOffset = rlpOffset + valueStart;
            node.ValueLength = (byte)valueLength;
        }
        else
        {
            if (nibbleCount == 0)
            {
                ThrowTrie("Extension with an empty prefix");
            }

            node.Kind = SparseNodeKind.Extension;
            byte first = rlp[secondItem];
            if (first != 0xA0 && (first < 0xC0 || first > 0xDE))
            {
                ThrowTrie($"Malformed extension child item 0x{first:x2}");
            }

            node.ChildSlice = ~(rlpOffset + secondItem);
        }
    }

    /// <summary>Content start and length of an RLP string item; throws if the item is a list.</summary>
    private static (int Start, int Length) StringContent(ReadOnlySpan<byte> rlp, int position)
    {
        byte first = rlp[position];
        if (first < 0x80)
        {
            return (position, 1);
        }

        if (first < 0xB8)
        {
            if (first == 0x81 && rlp[position + 1] < 0x80)
            {
                ThrowTrie("Non-canonical single-byte RLP string in sparse node");
            }

            return (position + 1, first - 0x80);
        }

        if (first == 0xB8)
        {
            int length = rlp[position + 1];
            if (length < 56)
            {
                ThrowTrie("Non-canonical long-form RLP string in sparse node");
            }

            return (position + 2, length);
        }

        ThrowTrie($"Expected an RLP string item, found 0x{first:x2}");
        return default;
    }

    // ---------------- Writes ----------------

    private (int Node, bool Changed) ApplyWrites(int nodeIndex, ref TreePath path, ReadOnlySpan<SparseTrieUpdate> updates, int start, int end)
    {
        if (nodeIndex < 0)
        {
            int created = CreateSubtree(ref path, updates, start, end);
            return (created, created >= 0);
        }

        ref SparseNode node = ref _arena.Node(nodeIndex);
        switch (node.Kind)
        {
            case SparseNodeKind.Leaf:
                return ApplyWritesToLeaf(nodeIndex, ref path, updates, start, end);
            case SparseNodeKind.Extension:
                return ApplyWritesToExtension(nodeIndex, ref path, updates, start, end);
            case SparseNodeKind.Branch:
                return ApplyWritesToBranch(nodeIndex, ref path, updates, start, end);
            default:
                ThrowTrie($"Cannot apply writes to {node.Kind} sparse node at {path}");
                return (nodeIndex, false);
        }
    }

    private (int Node, bool Changed) ApplyWritesToLeaf(int nodeIndex, ref TreePath path, ReadOnlySpan<SparseTrieUpdate> updates, int start, int end)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        Span<byte> prefix = _arena.Bytes(node.PrefixOffset, node.PrefixLength);
        int depth = path.Length;

        // Longest prefix of the leaf key shared by every non-delete update in the range.
        int shared = node.PrefixLength;
        bool hasWrite = false;
        for (int i = start; i < end; i++)
        {
            if (updates[i].IsDelete)
            {
                continue;
            }

            hasWrite = true;
            int lcp = 0;
            while (lcp < prefix.Length && KeyNibble(updates[i].Key, depth + lcp) == prefix[lcp])
            {
                lcp++;
            }

            if (lcp < shared)
            {
                shared = lcp;
            }
        }

        if (!hasWrite)
        {
            return (nodeIndex, false);
        }

        if (shared == node.PrefixLength)
        {
            // Every write hits the leaf key exactly (unique keys => a single write).
            return (nodeIndex, UpdateLeafValue(nodeIndex, updates, start, end));
        }

        // Split: branch at depth+shared; the leaf keeps its tail below its divergence nibble.
        int branchIndex = _arena.AllocNode();
        ref SparseNode branch = ref _arena.Node(branchIndex);
        branch.Kind = SparseNodeKind.Branch;
        branch.Flags = SparseNodeFlags.Dirty;

        int leafNibble = prefix[shared];
        ReattachWithShorterPrefix(nodeIndex, shared + 1);

        int result = branchIndex;
        if (shared > 0)
        {
            result = CreateExtension(prefix[..shared], branchIndex);
        }

        // Insert the leaf and every update group under the branch.
        SetBranchChild(branchIndex, leafNibble, nodeIndex);

        int branchDepth = depth + shared;
        int i2 = start;
        while (i2 < end)
        {
            int nibble = KeyNibble(updates[i2].Key, branchDepth);
            int groupEnd = i2 + 1;
            while (groupEnd < end && KeyNibble(updates[groupEnd].Key, branchDepth) == nibble)
            {
                groupEnd++;
            }

            TreePath childPath = path;
            for (int d = depth; d < branchDepth; d++)
            {
                childPath.AppendMut(KeyNibble(updates[i2].Key, d));
            }

            childPath.AppendMut(nibble);

            if (nibble == leafNibble)
            {
                (int updated, _) = ApplyWrites(nodeIndex, ref childPath, updates, i2, groupEnd);
                SetBranchChild(branchIndex, nibble, updated);
            }
            else
            {
                int created = CreateSubtree(ref childPath, updates, i2, groupEnd);
                if (created >= 0)
                {
                    SetBranchChild(branchIndex, nibble, created);
                }
            }

            i2 = groupEnd;
        }

        return (result, true);
    }

    private bool UpdateLeafValue(int nodeIndex, ReadOnlySpan<SparseTrieUpdate> updates, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (updates[i].IsDelete)
            {
                continue;
            }

            ref SparseNode node = ref _arena.Node(nodeIndex);
            byte[] value = updates[i].Value!;
            if (value.Length > MaxLeafValueLength)
            {
                ThrowTrie($"Leaf value of {value.Length} bytes exceeds the supported maximum");
            }

            if (value.AsSpan().SequenceEqual(_arena.Bytes(node.ValueOffset, node.ValueLength)))
            {
                return false;
            }

            if ((node.Flags & SparseNodeFlags.OwnedValue) != 0)
            {
                _arena.ReleaseBytes(node.ValueLength);
            }

            node.ValueOffset = _arena.AllocBytes(value.Length);
            value.CopyTo(_arena.Bytes(node.ValueOffset, value.Length));
            node.ValueLength = (byte)value.Length;
            node.Flags |= SparseNodeFlags.Dirty | SparseNodeFlags.OwnedValue;
            return true;
        }

        return false;
    }

    /// <summary>Drops the first <paramref name="consumedNibbles"/> nibbles of a node's prefix in place.</summary>
    private void ReattachWithShorterPrefix(int nodeIndex, int consumedNibbles)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        _arena.ReleaseBytes(consumedNibbles);
        node.PrefixOffset += consumedNibbles;
        node.PrefixLength = (byte)(node.PrefixLength - consumedNibbles);
        node.Flags |= SparseNodeFlags.Dirty;
    }

    private int CreateExtension(ReadOnlySpan<byte> prefixNibbles, int childIndex)
    {
        int index = _arena.AllocNode();
        ref SparseNode node = ref _arena.Node(index);
        node.Kind = SparseNodeKind.Extension;
        node.Flags = SparseNodeFlags.Dirty;
        node.PrefixOffset = _arena.AllocBytes(prefixNibbles.Length);
        prefixNibbles.CopyTo(_arena.Bytes(node.PrefixOffset, prefixNibbles.Length));
        node.PrefixLength = (byte)prefixNibbles.Length;
        node.ChildSlice = childIndex;
        return index;
    }

    /// <summary>Adds or replaces a branch child, growing the dense slice when the slot is new.</summary>
    private void SetBranchChild(int branchIndex, int nibble, int childIndex)
    {
        ref SparseNode branch = ref _arena.Node(branchIndex);
        int bit = 1 << nibble;
        if ((branch.OccupiedMask & bit) != 0)
        {
            _arena.ChildSlice(branch.ChildSlice, branch.ChildCount)[branch.ChildSlot(nibble)] = childIndex;
        }
        else
        {
            int oldCount = branch.ChildCount;
            int slot = branch.ChildSlot(nibble);
            int newSlice = _arena.AllocChildSlice(oldCount + 1);
            Span<int> oldChildren = oldCount > 0 ? _arena.ChildSlice(branch.ChildSlice, oldCount) : default;
            Span<int> newChildren = _arena.ChildSlice(newSlice, oldCount + 1);
            oldChildren[..slot].CopyTo(newChildren);
            newChildren[slot] = childIndex;
            oldChildren[slot..].CopyTo(newChildren[(slot + 1)..]);
            if (oldCount > 0)
            {
                _arena.ReleaseChildSlice(oldCount);
            }

            branch.ChildSlice = newSlice;
            branch.OccupiedMask = (ushort)(branch.OccupiedMask | bit);
        }

        branch.DirtyMask = (ushort)(branch.DirtyMask | bit);
        branch.Flags |= SparseNodeFlags.Dirty;
    }

    private (int Node, bool Changed) ApplyWritesToExtension(int nodeIndex, ref TreePath path, ReadOnlySpan<SparseTrieUpdate> updates, int start, int end)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        Span<byte> prefix = _arena.Bytes(node.PrefixOffset, node.PrefixLength);
        int depth = path.Length;

        // First diverging position among non-delete keys; prefix.Length when all pass through.
        int split = node.PrefixLength;
        bool hasWrite = false;
        for (int i = start; i < end; i++)
        {
            if (updates[i].IsDelete)
            {
                continue;
            }

            hasWrite = true;
            int lcp = 0;
            while (lcp < prefix.Length && KeyNibble(updates[i].Key, depth + lcp) == prefix[lcp])
            {
                lcp++;
            }

            if (lcp < split)
            {
                split = lcp;
            }
        }

        if (!hasWrite)
        {
            return (nodeIndex, false);
        }

        if (split == node.PrefixLength)
        {
            int prefixLength = node.PrefixLength;
            int childEntry = node.ChildSlice;
            if (childEntry < 0)
            {
                ThrowTrie($"Write target below an unrevealed extension child at {path}");
            }

            AppendNibbles(ref path, prefix);
            (int newChild, bool changed) = ApplyWrites(childEntry, ref path, updates, start, end);
            path.TruncateMut(path.Length - prefixLength);
            if (changed)
            {
                ref SparseNode ext = ref _arena.Node(nodeIndex);
                ext.ChildSlice = newChild;
                ext.Flags |= SparseNodeFlags.Dirty;
            }

            return (nodeIndex, changed);
        }

        // Split at `split`: branch there; the extension tail (or its child) hangs below it.
        int branchIndex = _arena.AllocNode();
        ref SparseNode branch = ref _arena.Node(branchIndex);
        branch.Kind = SparseNodeKind.Branch;
        branch.Flags = SparseNodeFlags.Dirty;

        int extNibble = prefix[split];
        int tail;
        if (split + 1 < node.PrefixLength)
        {
            ReattachWithShorterPrefix(nodeIndex, split + 1);
            tail = nodeIndex;
        }
        else
        {
            tail = AdoptChildEntry(node.ChildSlice);
            ValidateNodeDepth(tail, depth + split + 1);
            FreeNode(nodeIndex);
        }

        SetBranchChild(branchIndex, extNibble, tail);

        int result = branchIndex;
        if (split > 0)
        {
            result = CreateExtension(prefix[..split], branchIndex);
        }

        int branchDepth = depth + split;
        int i2 = start;
        while (i2 < end)
        {
            int nibble = KeyNibble(updates[i2].Key, branchDepth);
            int groupEnd = i2 + 1;
            while (groupEnd < end && KeyNibble(updates[groupEnd].Key, branchDepth) == nibble)
            {
                groupEnd++;
            }

            TreePath childPath = path;
            for (int d = depth; d < branchDepth; d++)
            {
                childPath.AppendMut(KeyNibble(updates[i2].Key, d));
            }

            childPath.AppendMut(nibble);

            if (nibble == extNibble)
            {
                (int updated, _) = ApplyWrites(tail, ref childPath, updates, i2, groupEnd);
                SetBranchChild(branchIndex, nibble, updated);
            }
            else
            {
                int created = CreateSubtree(ref childPath, updates, i2, groupEnd);
                if (created >= 0)
                {
                    SetBranchChild(branchIndex, nibble, created);
                }
            }

            i2 = groupEnd;
        }

        return (result, true);
    }

    private (int Node, bool Changed) ApplyWritesToBranch(int nodeIndex, ref TreePath path, ReadOnlySpan<SparseTrieUpdate> updates, int start, int end)
    {
        int depth = path.Length;
        bool changed = false;

        int i = start;
        while (i < end)
        {
            int nibble = KeyNibble(updates[i].Key, depth);
            int groupEnd = i + 1;
            while (groupEnd < end && KeyNibble(updates[groupEnd].Key, depth) == nibble)
            {
                groupEnd++;
            }

            bool groupHasWrite = false;
            for (int j = i; j < groupEnd; j++)
            {
                if (!updates[j].IsDelete)
                {
                    groupHasWrite = true;
                    break;
                }
            }

            if (!groupHasWrite)
            {
                i = groupEnd;
                continue;
            }

            ref SparseNode node = ref _arena.Node(nodeIndex);
            if ((node.OccupiedMask & (1 << nibble)) != 0)
            {
                int entry = _arena.ChildSlice(node.ChildSlice, node.ChildCount)[node.ChildSlot(nibble)];
                if (entry < 0)
                {
                    ThrowTrie($"Write target below an unrevealed child at {path}, nibble {nibble}");
                }

                path.AppendMut(nibble);
                (int newChild, bool childChanged) = ApplyWrites(entry, ref path, updates, i, groupEnd);
                path.TruncateMut(path.Length - 1);
                if (childChanged)
                {
                    SetBranchChild(nodeIndex, nibble, newChild);
                    changed = true;
                }
            }
            else
            {
                path.AppendMut(nibble);
                int created = CreateSubtree(ref path, updates, i, groupEnd);
                path.TruncateMut(path.Length - 1);
                if (created >= 0)
                {
                    SetBranchChild(nodeIndex, nibble, created);
                    changed = true;
                }
            }

            i = groupEnd;
        }

        return (nodeIndex, changed);
    }

    /// <summary>Builds the canonical subtree for the non-delete updates in the range; -1 when none.</summary>
    private int CreateSubtree(ref TreePath path, ReadOnlySpan<SparseTrieUpdate> updates, int start, int end)
    {
        int firstWrite = -1;
        int lastWrite = -1;
        int writeCount = 0;
        for (int i = start; i < end; i++)
        {
            if (!updates[i].IsDelete)
            {
                if (firstWrite < 0)
                {
                    firstWrite = i;
                }

                lastWrite = i;
                writeCount++;
            }
        }

        if (writeCount == 0)
        {
            return -1;
        }

        int depth = path.Length;
        if (writeCount == 1)
        {
            return CreateLeaf(updates[firstWrite].Key, depth, updates[firstWrite].Value!);
        }

        // Sorted keys: the common prefix of all is the common prefix of first and last.
        int shared = 0;
        while (depth + shared < MaxDepth && KeyNibble(updates[firstWrite].Key, depth + shared) == KeyNibble(updates[lastWrite].Key, depth + shared))
        {
            shared++;
        }

        int branchIndex = _arena.AllocNode();
        ref SparseNode branch = ref _arena.Node(branchIndex);
        branch.Kind = SparseNodeKind.Branch;
        branch.Flags = SparseNodeFlags.Dirty;

        int branchDepth = depth + shared;
        int i2 = start;
        while (i2 < end)
        {
            if (updates[i2].IsDelete)
            {
                i2++;
                continue;
            }

            int nibble = KeyNibble(updates[i2].Key, branchDepth);
            int groupEnd = i2 + 1;
            while (groupEnd < end && (updates[groupEnd].IsDelete || KeyNibble(updates[groupEnd].Key, branchDepth) == nibble))
            {
                groupEnd++;
            }

            TreePath childPath = path;
            for (int d = depth; d < branchDepth; d++)
            {
                childPath.AppendMut(KeyNibble(updates[i2].Key, d));
            }

            childPath.AppendMut(nibble);
            int created = CreateSubtree(ref childPath, updates, i2, groupEnd);
            if (created >= 0)
            {
                SetBranchChild(branchIndex, nibble, created);
            }

            i2 = groupEnd;
        }

        if (shared > 0)
        {
            Span<byte> prefix = stackalloc byte[shared];
            for (int d = 0; d < shared; d++)
            {
                prefix[d] = (byte)KeyNibble(updates[firstWrite].Key, depth + d);
            }

            return CreateExtension(prefix, branchIndex);
        }

        return branchIndex;
    }

    private int CreateLeaf(in ValueHash256 key, int depth, byte[] value)
    {
        if (value.Length > MaxLeafValueLength)
        {
            ThrowTrie($"Leaf value of {value.Length} bytes exceeds the supported maximum");
        }

        int index = _arena.AllocNode();
        ref SparseNode node = ref _arena.Node(index);
        node.Kind = SparseNodeKind.Leaf;
        node.Flags = SparseNodeFlags.Dirty | SparseNodeFlags.OwnedValue;

        int suffixLength = MaxDepth - depth;
        node.PrefixLength = (byte)suffixLength;
        if (suffixLength > 0)
        {
            node.PrefixOffset = _arena.AllocBytes(suffixLength);
            Span<byte> nibbles = _arena.Bytes(node.PrefixOffset, suffixLength);
            for (int i = 0; i < suffixLength; i++)
            {
                nibbles[i] = (byte)KeyNibble(key, depth + i);
            }
        }

        node.ValueOffset = _arena.AllocBytes(value.Length);
        value.CopyTo(_arena.Bytes(node.ValueOffset, value.Length));
        node.ValueLength = (byte)value.Length;
        return index;
    }

    /// <summary>
    /// Converts a child entry of <paramref name="ownerIndex"/> into a node index so it can be
    /// re-attached under a different parent: inline children materialize, hashed references
    /// become blinded nodes carrying only the hash.
    /// </summary>
    private int AdoptChildEntry(int entry)
    {
        if (entry >= 0)
        {
            return entry;
        }

        int materialized = TryMaterializeInline(entry, out ValueHash256 hash);
        if (materialized >= 0)
        {
            return materialized;
        }

        int index = _arena.AllocNode();
        ref SparseNode node = ref _arena.Node(index);
        node.Kind = SparseNodeKind.Blinded;
        node.Hash = hash;
        return index;
    }

    private void FreeNode(int nodeIndex)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        if (node.StagedRecord >= 0)
        {
            // Orphan the staged record so its owned array can be collected without publication.
            _staged!.GetRef(node.StagedRecord) = default;
        }

        _arena.FreeNode(nodeIndex);
    }

    // ---------------- Deletes ----------------

    private struct PendingCollapse
    {
        public TreePath BranchPath;
        public byte SurvivorNibble;
    }

    private void ApplyDeletes(ReadOnlySpan<SparseTrieUpdate> updates)
    {
        if (_rootNode < 0)
        {
            return;
        }

        using ArrayPoolList<PendingReveal> reveals = new(4);

        // Two swapped round buffers: cascading collapses run round after round, and renting a
        // fresh list per round copied the whole pending set each time.
        ArrayPoolList<PendingCollapse> collapses = new(4);
        ArrayPoolList<PendingCollapse>? round = null;
        try
        {
            round = new ArrayPoolList<PendingCollapse>(4);
            TreePath path = TreePath.Empty;
            _rootNode = DeleteWalk(_rootNode, ref path, updates, 0, updates.Length, reveals, collapses);

            while (collapses.Count > 0)
            {
                ResolvePendingSurvivors(reveals);
                reveals.Clear();

                (round, collapses) = (collapses, round);
                collapses.Clear();

                for (int i = 0; i < round.Count; i++)
                {
                    TreePath rootPath = TreePath.Empty;
                    _rootNode = CollapseAlongPath(_rootNode, ref rootPath, round[i].BranchPath, round[i].SurvivorNibble, reveals, collapses);
                }
            }
        }
        finally
        {
            collapses.Dispose();
            round?.Dispose();
        }
    }

    /// <summary>Resolves survivor reveals queued by delete collapses; parents are patched in place.</summary>
    private void ResolvePendingSurvivors(ArrayPoolList<PendingReveal> reveals)
    {
        if (reveals.Count > 0)
        {
            ResolvePending(reveals);
        }
    }

    private int DeleteWalk(int nodeIndex, ref TreePath path, ReadOnlySpan<SparseTrieUpdate> updates, int start, int end, ArrayPoolList<PendingReveal> reveals, ArrayPoolList<PendingCollapse> collapses)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        switch (node.Kind)
        {
            case SparseNodeKind.Leaf:
                {
                    Span<byte> prefix = _arena.Bytes(node.PrefixOffset, node.PrefixLength);
                    for (int i = start; i < end; i++)
                    {
                        if (updates[i].IsDelete && MatchesPrefix(updates[i].Key, path.Length, prefix))
                        {
                            FreeNode(nodeIndex);
                            return -1;
                        }
                    }

                    return nodeIndex;
                }

            case SparseNodeKind.Extension:
                {
                    Span<byte> prefix = _arena.Bytes(node.PrefixOffset, node.PrefixLength);
                    (int matchStart, int matchEnd) = FullPrefixMatchRange(updates, start, end, path.Length, prefix);
                    if (!HasDelete(updates, matchStart, matchEnd))
                    {
                        return nodeIndex;
                    }

                    int prefixLength = node.PrefixLength;
                    int childEntry = node.ChildSlice;
                    if (childEntry < 0)
                    {
                        ThrowTrie($"Delete target below an unrevealed extension child at {path}");
                    }

                    AppendNibbles(ref path, prefix);
                    int newChild = DeleteWalk(childEntry, ref path, updates, matchStart, matchEnd, reveals, collapses);
                    path.TruncateMut(path.Length - prefixLength);

                    return NormalizeExtension(nodeIndex, newChild);
                }

            case SparseNodeKind.Branch:
                {
                    int depth = path.Length;
                    int i = start;
                    while (i < end)
                    {
                        int nibble = KeyNibble(updates[i].Key, depth);
                        int groupEnd = i + 1;
                        while (groupEnd < end && KeyNibble(updates[groupEnd].Key, depth) == nibble)
                        {
                            groupEnd++;
                        }

                        if (HasDelete(updates, i, groupEnd) && (node.OccupiedMask & (1 << nibble)) != 0)
                        {
                            int entry = _arena.ChildSlice(node.ChildSlice, node.ChildCount)[node.ChildSlot(nibble)];
                            if (entry < 0)
                            {
                                ThrowTrie($"Delete target below an unrevealed child at {path}, nibble {nibble}");
                            }

                            path.AppendMut(nibble);
                            int newChild = DeleteWalk(entry, ref path, updates, i, groupEnd, reveals, collapses);
                            path.TruncateMut(path.Length - 1);

                            if (newChild < 0)
                            {
                                RemoveBranchChild(nodeIndex, nibble);
                            }
                            else if (newChild != entry)
                            {
                                SetBranchChild(nodeIndex, nibble, newChild);
                            }
                            else
                            {
                                ref SparseNode branchNode = ref _arena.Node(nodeIndex);
                                if (_arena.Node(newChild).IsDirty)
                                {
                                    branchNode.DirtyMask = (ushort)(branchNode.DirtyMask | (1 << nibble));
                                    branchNode.Flags |= SparseNodeFlags.Dirty;
                                }
                            }
                        }

                        i = groupEnd;
                    }

                    return NormalizeBranch(nodeIndex, ref path, reveals, collapses);
                }

            default:
                ThrowTrie($"Cannot delete below {node.Kind} sparse node at {path}");
                return nodeIndex;
        }
    }

    private static bool HasDelete(ReadOnlySpan<SparseTrieUpdate> updates, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (updates[i].IsDelete)
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveBranchChild(int branchIndex, int nibble)
    {
        ref SparseNode branch = ref _arena.Node(branchIndex);
        int oldCount = branch.ChildCount;
        int slot = branch.ChildSlot(nibble);
        Span<int> oldChildren = _arena.ChildSlice(branch.ChildSlice, oldCount);
        if (oldCount > 1)
        {
            int newSlice = _arena.AllocChildSlice(oldCount - 1);
            Span<int> newChildren = _arena.ChildSlice(newSlice, oldCount - 1);
            oldChildren[..slot].CopyTo(newChildren);
            oldChildren[(slot + 1)..].CopyTo(newChildren[slot..]);
            branch.ChildSlice = newSlice;
        }

        _arena.ReleaseChildSlice(oldCount);
        branch.OccupiedMask = (ushort)(branch.OccupiedMask & ~(1 << nibble));
        branch.DirtyMask = (ushort)(branch.DirtyMask & ~(1 << nibble));
        branch.Flags |= SparseNodeFlags.Dirty;
    }

    /// <summary>
    /// Applies canonical collapse to a branch after deletions: zero children removes it, a single
    /// child merges upward. An unrevealed survivor defers the collapse until its reveal resolves.
    /// </summary>
    private int NormalizeBranch(int branchIndex, ref TreePath path, ArrayPoolList<PendingReveal> reveals, ArrayPoolList<PendingCollapse> collapses)
    {
        ref SparseNode branch = ref _arena.Node(branchIndex);
        int count = branch.ChildCount;
        if (count == 0)
        {
            FreeNode(branchIndex);
            return -1;
        }

        if (count > 1)
        {
            return branchIndex;
        }

        int nibble = BitOperations.TrailingZeroCount(branch.OccupiedMask);
        int entry = _arena.ChildSlice(branch.ChildSlice, 1)[0];
        if (entry < 0)
        {
            int offset = ~entry;
            byte first = _arena.Bytes(offset, 1)[0];
            if (first == 0xA0)
            {
                return DeferCollapse(branchIndex, nibble, new ValueHash256(_arena.Bytes(offset + 1, 32)), ref path, reveals, collapses);
            }

            entry = AdoptChildEntry(entry);
            ValidateNodeDepth(entry, path.Length + 1);
        }
        else if (_arena.Node(entry).Kind == SparseNodeKind.Blinded)
        {
            return DeferCollapse(branchIndex, nibble, _arena.Node(entry).Hash, ref path, reveals, collapses);
        }

        return MergeCollapsedBranch(branchIndex, nibble, entry);
    }

    /// <summary>
    /// Defers a collapse whose survivor is unrevealed: its reveal is batched and the branch is
    /// revisited by path once the survivor is materialized. The flag keeps a later normalization
    /// of the same branch from queuing it twice.
    /// </summary>
    private int DeferCollapse(int branchIndex, int nibble, in ValueHash256 survivorHash, ref TreePath path, ArrayPoolList<PendingReveal> reveals, ArrayPoolList<PendingCollapse> collapses)
    {
        ref SparseNode branch = ref _arena.Node(branchIndex);
        if ((branch.Flags & SparseNodeFlags.CollapsePending) == 0)
        {
            branch.Flags |= SparseNodeFlags.CollapsePending;
            TreePath survivorPath = path;
            survivorPath.AppendMut(nibble);
            reveals.Add(new PendingReveal
            {
                Path = survivorPath,
                Hash = survivorHash,
                Parent = branchIndex,
                ChildNibble = (byte)nibble,
            });
            collapses.Add(new PendingCollapse { BranchPath = path, SurvivorNibble = (byte)nibble });
        }

        return branchIndex;
    }

    /// <summary>Replaces a single-child branch by its survivor with the branch nibble prepended.</summary>
    private int MergeCollapsedBranch(int branchIndex, int nibble, int survivorIndex)
    {
        FreeNode(branchIndex);

        ref SparseNode survivor = ref _arena.Node(survivorIndex);
        switch (survivor.Kind)
        {
            case SparseNodeKind.Leaf:
            case SparseNodeKind.Extension:
                {
                    PrependNibble(survivorIndex, nibble);
                    return survivorIndex;
                }

            case SparseNodeKind.Branch:
                {
                    Span<byte> prefix = [(byte)nibble];
                    return CreateExtension(prefix, survivorIndex);
                }

            default:
                ThrowTrie($"Cannot collapse into {survivor.Kind} sparse node");
                return survivorIndex;
        }
    }

    private void PrependNibble(int nodeIndex, int nibble)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        int newLength = node.PrefixLength + 1;
        if (newLength > MaxDepth)
        {
            ThrowTrie($"Collapse would grow a prefix beyond {MaxDepth} nibbles");
        }
        int newOffset = _arena.AllocBytes(newLength);
        Span<byte> nibbles = _arena.Bytes(newOffset, newLength);
        nibbles[0] = (byte)nibble;
        if (node.PrefixLength > 0)
        {
            _arena.Bytes(node.PrefixOffset, node.PrefixLength).CopyTo(nibbles[1..]);
            _arena.ReleaseBytes(node.PrefixLength);
        }

        node.PrefixOffset = newOffset;
        node.PrefixLength = (byte)newLength;
        node.Flags |= SparseNodeFlags.Dirty;
    }

    /// <summary>Merges an extension with its (possibly replaced) child per canonical rules.</summary>
    private int NormalizeExtension(int extIndex, int newChild)
    {
        ref SparseNode ext = ref _arena.Node(extIndex);
        if (newChild < 0)
        {
            FreeNode(extIndex);
            return -1;
        }

        ref SparseNode child = ref _arena.Node(newChild);
        if (newChild == ext.ChildSlice && !child.IsDirty)
        {
            return extIndex;
        }

        switch (child.Kind)
        {
            case SparseNodeKind.Branch:
                ext.ChildSlice = newChild;
                ext.Flags |= SparseNodeFlags.Dirty;
                return extIndex;

            case SparseNodeKind.Leaf:
            case SparseNodeKind.Extension:
                {
                    // Merge prefixes: the extension disappears into the child.
                    int extLength = ext.PrefixLength;
                    int childLength = child.PrefixLength;
                    if (extLength + childLength > MaxDepth)
                    {
                        ThrowTrie($"Extension merge would grow a prefix beyond {MaxDepth} nibbles");
                    }

                    int newOffset = _arena.AllocBytes(extLength + childLength);
                    Span<byte> nibbles = _arena.Bytes(newOffset, extLength + childLength);
                    _arena.Bytes(ext.PrefixOffset, extLength).CopyTo(nibbles);
                    if (childLength > 0)
                    {
                        _arena.Bytes(child.PrefixOffset, childLength).CopyTo(nibbles[extLength..]);
                        _arena.ReleaseBytes(childLength);
                    }

                    child.PrefixOffset = newOffset;
                    child.PrefixLength = (byte)(extLength + childLength);
                    child.Flags |= SparseNodeFlags.Dirty;
                    FreeNode(extIndex);
                    return newChild;
                }

            default:
                ThrowTrie($"Cannot merge extension into {child.Kind} sparse node");
                return extIndex;
        }
    }

    /// <summary>
    /// Re-descends to a deferred collapse site by nibble path and normalizes upward. Records can
    /// go stale — an earlier collapse in the same round may already have merged the branch or
    /// restructured its ancestors — so any mismatch along the descent is treated as
    /// already-handled and left unchanged.
    /// </summary>
    private int CollapseAlongPath(int nodeIndex, ref TreePath path, in TreePath targetPath, byte survivorNibble, ArrayPoolList<PendingReveal> reveals, ArrayPoolList<PendingCollapse> collapses)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);
        if (path.Length >= targetPath.Length)
        {
            if (path.Length == targetPath.Length
                && node.Kind == SparseNodeKind.Branch
                && node.ChildCount == 1
                && BitOperations.TrailingZeroCount(node.OccupiedMask) == survivorNibble)
            {
                int survivor = _arena.ChildSlice(node.ChildSlice, 1)[0];
                if (survivor < 0)
                {
                    ThrowTrie($"Deferred collapse survivor still unrevealed at {path}");
                }

                node.Flags &= ~SparseNodeFlags.CollapsePending;
                return MergeCollapsedBranch(nodeIndex, survivorNibble, survivor);
            }

            return nodeIndex;
        }

        switch (node.Kind)
        {
            case SparseNodeKind.Extension:
                {
                    int prefixLength = node.PrefixLength;
                    Span<byte> prefix = _arena.Bytes(node.PrefixOffset, prefixLength);
                    if (path.Length + prefixLength > targetPath.Length || node.ChildSlice < 0)
                    {
                        return nodeIndex;
                    }

                    for (int i = 0; i < prefixLength; i++)
                    {
                        if (targetPath[path.Length + i] != prefix[i])
                        {
                            return nodeIndex;
                        }
                    }

                    AppendNibbles(ref path, prefix);
                    int newChild = CollapseAlongPath(node.ChildSlice, ref path, targetPath, survivorNibble, reveals, collapses);
                    path.TruncateMut(path.Length - prefixLength);
                    return NormalizeExtension(nodeIndex, newChild);
                }

            case SparseNodeKind.Branch:
                {
                    int nibble = targetPath[path.Length];
                    if ((node.OccupiedMask & (1 << nibble)) == 0)
                    {
                        return nodeIndex;
                    }

                    int entry = _arena.ChildSlice(node.ChildSlice, node.ChildCount)[node.ChildSlot(nibble)];
                    if (entry < 0)
                    {
                        return nodeIndex;
                    }

                    path.AppendMut(nibble);
                    int newChild = CollapseAlongPath(entry, ref path, targetPath, survivorNibble, reveals, collapses);
                    path.TruncateMut(path.Length - 1);
                    if (newChild != entry)
                    {
                        SetBranchChild(nodeIndex, nibble, newChild);
                    }

                    return NormalizeBranch(nodeIndex, ref path, reveals, collapses);
                }

            default:
                return nodeIndex;
        }
    }

    // ---------------- Encode ----------------

    private void EncodeNode(int nodeIndex, ref TreePath path, bool isRoot)
    {
        ref SparseNode node = ref _arena.Node(nodeIndex);

        // Children first (post-order), so their hashes/inline bytes are final.
        if (node.Kind == SparseNodeKind.Extension)
        {
            int entry = node.ChildSlice;
            if (entry >= 0 && _arena.Node(entry).IsDirty)
            {
                int prefixLength = node.PrefixLength;
                AppendNibbles(ref path, _arena.Bytes(node.PrefixOffset, prefixLength));
                EncodeNode(entry, ref path, isRoot: false);
                path.TruncateMut(path.Length - prefixLength);
            }
        }
        else if (node.Kind == SparseNodeKind.Branch)
        {
            int dirtyMask = node.DirtyMask;
            while (dirtyMask != 0)
            {
                int nibble = BitOperations.TrailingZeroCount(dirtyMask);
                dirtyMask &= dirtyMask - 1;
                int entry = _arena.ChildSlice(node.ChildSlice, node.ChildCount)[node.ChildSlot(nibble)];
                if (entry >= 0 && _arena.Node(entry).IsDirty)
                {
                    path.AppendMut(nibble);
                    EncodeNode(entry, ref path, isRoot: false);
                    path.TruncateMut(path.Length - 1);
                }
            }

            node.DirtyMask = 0;
        }

        // Child reference lengths are computed once and shared between sizing and writing.
        Span<int> childRefLengths = stackalloc int[16];
        int contentLength = ComputeContentLength(ref node, childRefLengths);
        int totalLength = Rlp.LengthOfSequence(contentLength);

        if (totalLength < 32 && !isRoot)
        {
            int handle = _arena.AllocBytes(totalLength);
            WriteNodeRlp(ref node, _arena.Bytes(handle, totalLength), contentLength, childRefLengths);
            if (node.StagedRecord >= 0)
            {
                _staged!.GetRef(node.StagedRecord) = default;
                node.StagedRecord = -1;
            }

            // A leaf's previous owned RLP region dies here; a value still aliasing it is moved
            // out first. Branch/extension regions may still back negative child entries, so
            // they die with the node instead.
            if (node.Kind == SparseNodeKind.Leaf && (node.Flags & SparseNodeFlags.OwnedRlp) != 0)
            {
                if ((node.Flags & SparseNodeFlags.OwnedValue) == 0)
                {
                    int movedValue = _arena.AllocBytes(node.ValueLength);
                    _arena.Bytes(node.ValueOffset, node.ValueLength).CopyTo(_arena.Bytes(movedValue, node.ValueLength));
                    node.ValueOffset = movedValue;
                    node.Flags |= SparseNodeFlags.OwnedValue;
                }

                _arena.ReleaseBytes(node.RlpLength);
            }

            node.RlpOffset = handle;
            node.RlpLength = (ushort)totalLength;
            node.Hash = default; // the hash no longer describes the current (embedded) encoding
            node.Flags = (node.Flags | SparseNodeFlags.Inline | SparseNodeFlags.OwnedRlp)
                & ~(SparseNodeFlags.Dirty | SparseNodeFlags.Unpublished);
            return;
        }

        byte[] rlp = GC.AllocateUninitializedArray<byte>(totalLength);
        WriteNodeRlp(ref node, rlp, contentLength, childRefLengths);
        node.Hash = ValueKeccak.Compute(rlp);

        // A node structurally restored to its committed encoding is re-staged; publishing a
        // byte-identical node is an idempotent no-op, and detecting the restore would require
        // retaining every node's original hash.
        if (node.StagedRecord >= 0)
        {
            _staged!.GetRef(node.StagedRecord) = default;
        }

        _staged ??= new ArrayPoolList<SparseTrieStagedNode>(64);
        node.StagedRecord = _staged.Count;
        _staged.Add(new SparseTrieStagedNode(path, node.Hash, rlp));
        node.Flags = (node.Flags | SparseNodeFlags.Unpublished) & ~(SparseNodeFlags.Dirty | SparseNodeFlags.Inline);
    }

    private int ComputeContentLength(ref SparseNode node, Span<int> childRefLengths)
    {
        switch (node.Kind)
        {
            case SparseNodeKind.Leaf:
                return HexPrefixRlpLength(node.PrefixLength) + Rlp.LengthOf(_arena.Bytes(node.ValueOffset, node.ValueLength));

            case SparseNodeKind.Extension:
                childRefLengths[0] = ChildRefLength(node.ChildSlice);
                return HexPrefixRlpLength(node.PrefixLength) + childRefLengths[0];

            case SparseNodeKind.Branch:
                {
                    int length = 1; // empty 17th value item
                    Span<int> children = _arena.ChildSlice(node.ChildSlice, node.ChildCount);
                    int slot = 0;
                    foreach (int entry in children)
                    {
                        length += childRefLengths[slot++] = ChildRefLength(entry);
                    }

                    length += 16 - slot; // one 0x80 byte per empty child
                    return length;
                }

            default:
                ThrowTrie($"Cannot encode {node.Kind} sparse node");
                return 0;
        }
    }

    private static int HexPrefixRlpLength(int nibbleCount)
    {
        int byteLength = nibbleCount / 2 + 1;
        // The hex-prefix flag byte is always < 0x80, so a single byte encodes as itself.
        return byteLength == 1 ? 1 : 1 + byteLength;
    }

    private int ChildRefLength(int entry)
    {
        if (entry >= 0)
        {
            ref SparseNode child = ref _arena.Node(entry);
            return child.IsInline ? child.RlpLength : Rlp.LengthOfKeccakRlp;
        }

        byte first = _arena.Bytes(~entry, 1)[0];
        return first == 0xA0 ? 33 : first - 0xC0 + 1;
    }

    private void WriteNodeRlp(ref SparseNode node, Span<byte> destination, int contentLength, ReadOnlySpan<int> childRefLengths)
    {
        int position = Rlp.StartSequence(destination, 0, contentLength);
        switch (node.Kind)
        {
            case SparseNodeKind.Leaf:
                position = WriteHexPrefix(destination, position, _arena.Bytes(node.PrefixOffset, node.PrefixLength), isLeaf: true);
                Rlp.Encode(destination, position, (ReadOnlySpan<byte>)_arena.Bytes(node.ValueOffset, node.ValueLength));
                return;

            case SparseNodeKind.Extension:
                position = WriteHexPrefix(destination, position, _arena.Bytes(node.PrefixOffset, node.PrefixLength), isLeaf: false);
                WriteChildRef(destination, position, node.ChildSlice, childRefLengths[0]);
                return;

            case SparseNodeKind.Branch:
                {
                    Span<int> children = _arena.ChildSlice(node.ChildSlice, node.ChildCount);
                    int occupied = node.OccupiedMask;
                    int slot = 0;
                    for (int nibble = 0; nibble < 16; nibble++)
                    {
                        if ((occupied & (1 << nibble)) != 0)
                        {
                            position = WriteChildRef(destination, position, children[slot], childRefLengths[slot]);
                            slot++;
                        }
                        else
                        {
                            destination[position++] = 0x80;
                        }
                    }

                    destination[position] = 0x80;
                    return;
                }
        }
    }

    private int WriteChildRef(Span<byte> destination, int position, int entry, int refLength)
    {
        if (entry >= 0)
        {
            ref SparseNode child = ref _arena.Node(entry);
            if (child.IsInline)
            {
                _arena.Bytes(child.RlpOffset, refLength).CopyTo(destination[position..]);
                return position + refLength;
            }

            destination[position] = 0xA0;
            child.Hash.Bytes.CopyTo(destination[(position + 1)..]);
            return position + 33;
        }

        _arena.Bytes(~entry, refLength).CopyTo(destination[position..]);
        return position + refLength;
    }

    private static int WriteHexPrefix(Span<byte> destination, int position, ReadOnlySpan<byte> nibbles, bool isLeaf)
    {
        int byteLength = nibbles.Length / 2 + 1;
        if (byteLength > 1)
        {
            destination[position++] = (byte)(0x80 + byteLength);
        }

        bool odd = (nibbles.Length & 1) != 0;
        byte flag = (byte)((isLeaf ? 0x20 : 0x00) | (odd ? 0x10 : 0x00));
        int i = 0;
        if (odd)
        {
            destination[position++] = (byte)(flag | nibbles[0]);
            i = 1;
        }
        else
        {
            destination[position++] = flag;
        }

        for (; i < nibbles.Length; i += 2)
        {
            destination[position++] = (byte)((nibbles[i] << 4) | nibbles[i + 1]);
        }

        return position;
    }
}
