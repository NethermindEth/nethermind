// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// The 256-leaf subtree of one stem, stored as its live nodes in DFS post-order followed by a compact
/// two-level leaf-presence bitmap and a format byte.
/// </summary>
/// <remarks>
/// The blob is <c>[ node entries: 32B × L ][ two-level bitmap footer ]</c>, where <c>L</c> is the live
/// node count (present leaves plus every internal whose range holds a present leaf). Entries are in DFS
/// post-order: a leaf entry holds its 32-byte value, an internal entry holds its cached child pair-hash.
/// A node's presence and slot are implied entirely by the leaf bitmap (internal liveness =
/// <see cref="RangeHasLeaf"/>), so no per-node offsets are stored. The trailing footer is the two-level
/// bitmap of <see cref="TwoLevelBitmapReader"/>. Zero values are normalized to absent. An empty blob is
/// an empty array (no footer) and signals stem deletion.
/// </remarks>
public static class StemLeafBlob
{
    public const int ValueLength = 32;
    private const int LeafCount = 256;

    public static bool TryGetValue(ReadOnlySpan<byte> blob, byte subIndex, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (blob.IsEmpty) return false;

        TwoLevelBitmapReader reader = TwoLevelBitmapReader.FromBlob(blob, out ReadOnlySpan<byte> entries);
        if (!reader.IsPresent(subIndex)) return false;

        Span<byte> bitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        reader.ExpandTo(bitmap);
        int slot = 0;
        Locate(bitmap, 0, LeafCount, subIndex, ref slot);
        value = entries.Slice(slot * ValueLength, ValueLength);
        return true;
    }

    /// <summary>
    /// Applies <paramref name="changes"/> (each a 32-byte leaf value; a zero value clears the leaf) to
    /// <paramref name="blob"/>, returning a disposable <see cref="RebuildState"/> whose <see cref="RebuildState.Blob"/>
    /// is the new blob (empty, with <see cref="RebuildState.IsEmpty"/> set, when no leaves remain) and whose
    /// <see cref="RebuildState.SubtreeRoot"/> is the merkelized 256-leaf subtree root.
    /// </summary>
    /// <remarks>
    /// Rebuilds dirty paths in post-order and copies clean subtree entries verbatim. A present leaf
    /// contributes <c>blake3(value)</c>; higher levels use the EIP-8297 pair hash, with empty subtrees
    /// folding to zero. The blob is backed by memory from <paramref name="provider"/>, so the caller must
    /// dispose the result once its bytes have been consumed.
    /// </remarks>
    internal static RebuildState Apply(ReadOnlySpan<byte> blob, IPbtStemChanges changes, IRefCountingMemoryProvider provider)
    {
        Span<byte> previousBitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        ReadOnlySpan<byte> previousEntries = default;
        if (blob.IsEmpty) previousBitmap.Clear();
        else TwoLevelBitmapReader.FromBlob(blob, out previousEntries).ExpandTo(previousBitmap);

        Span<byte> newBitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        previousBitmap.CopyTo(newBitmap);

        int changeCount = changes.Count;
        // The map keeps writes ascending by sub-index, so they can be read back in order without sorting.
        for (int i = 0; i < changeCount; i++) SetPresent(newBitmap, changes.SubIndexAt(i), changes.Get(i) != default);

        if (!RangeHasLeaf(newBitmap, 0, LeafCount)) return default;

        int liveCount = CountLiveNodes(newBitmap, 0, LeafCount);
        int groupCount = TwoLevelBitmapReader.OccupiedGroupsOf(newBitmap);
        RebuildState state = new(previousEntries, changes, liveCount, groupCount, provider);
        state.Build(previousBitmap, newBitmap);
        return state;
    }

    /// <summary>The stem node hash: <c>blake3(stem || 0x00 || subtreeRoot)</c>.</summary>
    public static ValueHash256 ComputeStemNodeHash(in Stem stem, in ValueHash256 subtreeRoot)
    {
        ValueHash256 left = default;
        stem.Bytes.CopyTo(left.BytesAsSpan);
        return Blake3Hash.HashPairOrZero(left, subtreeRoot);
    }

    private static bool IsPresent(ReadOnlySpan<byte> bitmap, byte subIndex) =>
        (bitmap[subIndex >> 3] & (1 << (7 - (subIndex & 7)))) != 0;

    private static void SetPresent(Span<byte> bitmap, byte subIndex, bool present)
    {
        byte mask = (byte)(1 << (7 - (subIndex & 7)));
        if (present)
        {
            bitmap[subIndex >> 3] |= mask;
        }
        else
        {
            bitmap[subIndex >> 3] &= (byte)~mask;
        }
    }

    private static bool RangeHasLeaf(ReadOnlySpan<byte> bitmap, int low, int high)
    {
        int firstByte = low >> 3;
        int lastByte = (high - 1) >> 3;
        for (int byteIndex = firstByte; byteIndex <= lastByte; byteIndex++)
        {
            int from = byteIndex == firstByte ? low & 7 : 0;
            int to = byteIndex == lastByte && (high & 7) != 0 ? high & 7 : 8;
            byte mask = (byte)((0xff >> from) & (0xff << (8 - to)));
            if (BitOperations.PopCount((uint)(bitmap[byteIndex] & mask)) != 0) return true;
        }

        return false;
    }

    /// <summary>The number of live nodes a rebuild emits for <paramref name="bitmap"/>: present leaves plus every internal node whose range holds one.</summary>
    private static int CountLiveNodes(ReadOnlySpan<byte> bitmap, int low, int high)
    {
        if (!RangeHasLeaf(bitmap, low, high)) return 0;
        if (high - low == 1) return 1;

        int middle = low + (high - low) / 2;
        return 1 + CountLiveNodes(bitmap, low, middle) + CountLiveNodes(bitmap, middle, high);
    }

    /// <summary>
    /// Accumulates into <paramref name="liveBefore"/> the number of live nodes whose post-order position
    /// precedes leaf <paramref name="target"/> — i.e. its slot in the post-order entry array. Post-order
    /// visits a subtree as <c>(left)&lt;(right)&lt;(root)</c>, so only whole subtrees entirely left of the
    /// target are counted wholesale; every enclosing internal node comes after the target and is skipped.
    /// </summary>
    private static void Locate(ReadOnlySpan<byte> bitmap, int low, int high, int target, ref int liveBefore)
    {
        if (high - low == 1) return;

        int middle = low + (high - low) / 2;
        if (target < middle)
        {
            Locate(bitmap, low, middle, target, ref liveBefore);
        }
        else
        {
            liveBefore += CountLiveNodes(bitmap, low, middle);
            Locate(bitmap, middle, high, target, ref liveBefore);
        }
    }

    private enum NodeKind : byte { Empty, Leaf, Internal }

    /// <summary>A reference to a rebuilt node: its kind and, for a leaf or internal node, its slot in the new blob.</summary>
    private readonly record struct NodeRef(NodeKind Kind, int Slot)
    {
        public static readonly NodeRef Empty = new(NodeKind.Empty, 0);
        public static NodeRef Leaf(int slot) => new(NodeKind.Leaf, slot);
        public static NodeRef Internal(int slot) => new(NodeKind.Internal, slot);
    }

    /// <remarks>
    /// Rebuilds directly into a final blob: the live nodes fill the front in post-order, then the two-level
    /// bitmap footer is appended. The recursion hands each subtree up as a <see cref="NodeRef"/> into that
    /// buffer, so a node's hash is materialized only where a parent needs it, never copied by value up the
    /// stack. The previous and new flat leaf bitmaps are threaded through as scoped parameters (they are the
    /// caller's stack buffers and must not escape). The buffer comes from an <see cref="IRefCountingMemoryProvider"/>
    /// and is released on <see cref="Dispose"/>; a <c>default</c> instance owns no buffer and represents the
    /// empty (no leaves remaining) result.
    /// </remarks>
    internal ref struct RebuildState
    {
        private readonly ReadOnlySpan<byte> _previousEntries;
        private readonly IPbtStemChanges _changes;
        private readonly Span<byte> _buffer;
        private RefCountingMemory? _memory;
        private int _previousSlot;
        private int _changeIndex;
        private int _slot;

        /// <summary>The rebuilt blob, valid until <see cref="Dispose"/>; empty when <see cref="IsEmpty"/>.</summary>
        public readonly ReadOnlySpan<byte> Blob => _buffer;

        /// <summary>Whether the stem has no leaves remaining, in which case <see cref="Blob"/> is empty.</summary>
        public readonly bool IsEmpty => _memory is null;

        /// <summary>The merkelized 256-leaf subtree root, populated by <see cref="Build"/>.</summary>
        public ValueHash256 SubtreeRoot { get; private set; }

        public RebuildState(
            ReadOnlySpan<byte> previousEntries,
            IPbtStemChanges changes,
            int liveCount,
            int groupCount,
            IRefCountingMemoryProvider provider)
        {
            int length = liveCount * ValueLength
                + groupCount * TwoLevelBitmapReader.SubWordLength
                + TwoLevelBitmapReader.TopLength
                + TwoLevelBitmapReader.FormatLength;
            _memory = provider.Rent(length);
            _buffer = _memory.GetSpan();

            _previousEntries = previousEntries;
            _changes = changes;
            _previousSlot = 0;
            _changeIndex = 0;
            _slot = 0;
        }

        /// <summary>Runs the post-order rebuild into <see cref="Blob"/>, sets <see cref="SubtreeRoot"/>, and appends the footer.</summary>
        public void Build(scoped ReadOnlySpan<byte> previousBitmap, scoped ReadOnlySpan<byte> newBitmap)
        {
            SubtreeRoot = Resolve(Rebuild(previousBitmap, newBitmap, 0, LeafCount));
            int footerLength = TwoLevelBitmapReader.Encode(newBitmap, _buffer[(_slot * ValueLength)..]);
            Debug.Assert(_slot * ValueLength + footerLength == _buffer.Length);
        }

        public void Dispose()
        {
            RefCountingMemory? memory = _memory;
            if (memory is not null)
            {
                _memory = null;
                ((IDisposable)memory).Dispose();
            }
        }

        private NodeRef Rebuild(scoped ReadOnlySpan<byte> previousBitmap, scoped ReadOnlySpan<byte> newBitmap, int low, int high)
        {
            if (_changeIndex >= _changes.Count || _changes.SubIndexAt(_changeIndex) >= high)
            {
                return CopyCleanSubtree(previousBitmap, low, high);
            }

            if (high - low == 1)
            {
                Debug.Assert(_changes.SubIndexAt(_changeIndex) == low);
                if (IsPresent(previousBitmap, (byte)low)) _previousSlot++;

                ValueHash256 value = _changes.Get(_changeIndex++);
                if (value == default) return NodeRef.Empty;

                return NodeRef.Leaf(Append(value.Bytes));
            }

            int middle = low + (high - low) / 2;
            NodeRef left = Rebuild(previousBitmap, newBitmap, low, middle);
            NodeRef right = Rebuild(previousBitmap, newBitmap, middle, high);

            if (RangeHasLeaf(previousBitmap, low, high)) _previousSlot++;
            if (!RangeHasLeaf(newBitmap, low, high)) return NodeRef.Empty;

            ValueHash256 hash = Blake3Hash.HashPairOrZero(Resolve(left), Resolve(right));
            return NodeRef.Internal(Append(hash.Bytes));
        }

        /// <summary>Materializes the hash a rebuilt node contributes to its parent.</summary>
        /// <remarks>
        /// A leaf entry stores its value, so its contributed hash is <c>blake3(value)</c>; an internal
        /// entry stores its already-cached hash, returned verbatim; an empty subtree folds to zero.
        /// </remarks>
        private readonly ValueHash256 Resolve(in NodeRef node) => node.Kind switch
        {
            NodeKind.Empty => default,
            NodeKind.Leaf => Blake3Hash.Hash(NodeAt(node.Slot)),
            _ => new ValueHash256(NodeAt(node.Slot)),
        };

        /// <summary>
        /// Copies an unchanged subtree's live nodes verbatim from the previous blob. Their count is
        /// <see cref="CountLiveNodes"/> of the previous bitmap over the subtree — the same contiguous
        /// post-order run the previous rebuild emitted, ending at the subtree root.
        /// </summary>
        private NodeRef CopyCleanSubtree(scoped ReadOnlySpan<byte> previousBitmap, int low, int high)
        {
            int count = CountLiveNodes(previousBitmap, low, high);
            if (count == 0) return NodeRef.Empty;

            _previousEntries.Slice(_previousSlot * ValueLength, count * ValueLength)
                .CopyTo(_buffer.Slice(_slot * ValueLength, count * ValueLength));
            _previousSlot += count;
            _slot += count;

            int rootSlot = _slot - 1;
            return high - low == 1 ? NodeRef.Leaf(rootSlot) : NodeRef.Internal(rootSlot);
        }

        private int Append(ReadOnlySpan<byte> node)
        {
            node.CopyTo(_buffer.Slice(_slot * ValueLength, ValueLength));
            return _slot++;
        }

        private readonly ReadOnlySpan<byte> NodeAt(int slot) => _buffer.Slice(slot * ValueLength, ValueLength);
    }
}
