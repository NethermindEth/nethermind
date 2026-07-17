// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// The 256-leaf subtree of one stem, stored as its leaves and branching internals in DFS post-order
/// followed by a compact two-level leaf-presence bitmap and a format byte.
/// </summary>
/// <remarks>
/// The blob is <c>[ node entries: 32B × S ][ two-level bitmap footer ]</c>. Entries are in DFS post-order:
/// a leaf entry holds its 32-byte value, an internal entry holds its cached child pair-hash. A node's
/// presence and slot are implied entirely by the leaf bitmap, so no per-node offsets are stored. The
/// trailing footer is the two-level bitmap of <see cref="TwoLevelBitmapReader"/>. Zero values are
/// normalized to absent. An empty blob is an empty array (no footer) and signals stem deletion.
/// <para>
/// The stored nodes are the present leaves plus only the <em>branching</em> internals — those with a leaf
/// in both halves. A single-child internal is recomputed on demand rather than cached, which makes
/// <c>S = 2n - 1</c> for <c>n</c> present leaves, whatever the placement of those leaves. Skipping is a
/// storage decision alone: a single-child internal still hashes as <c>HashPairOrZero(child, 0)</c>, which
/// is not its child's hash, so the subtree root is unaffected.
/// </para>
/// <para>
/// The legacy format (<see cref="TwoLevelBitmapReader.LegacyFormatByte"/>) cached every single-child
/// internal too, making <c>S</c> the live-node count of <see cref="CountLiveNodes"/>. It is read but never
/// written; <see cref="Apply"/> upgrades a legacy blob before rebuilding it.
/// </para>
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
        Locate(bitmap, 0, LeafCount, subIndex, TwoLevelBitmapReader.IsLegacy(blob), ref slot);
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
        if (!blob.IsEmpty && TwoLevelBitmapReader.IsLegacy(blob))
        {
            // A legacy post-order run holds nodes this format omits, so RebuildState cannot copy one verbatim.
            // The rebuilt blob owns its own memory and reads nothing back from the prior, so releasing the
            // upgraded copy the moment Apply returns is safe.
            using RefCountingMemory upgraded = Upgrade(blob, provider);
            return Apply(upgraded.GetSpan(), changes, provider);
        }

        Span<byte> previousBitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        ReadOnlySpan<byte> previousEntries = default;
        if (blob.IsEmpty) previousBitmap.Clear();
        else TwoLevelBitmapReader.FromBlob(blob, out previousEntries).ExpandTo(previousBitmap);

        Span<byte> newBitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        previousBitmap.CopyTo(newBitmap);

        int changeCount = changes.Count;
        // The map keeps writes ascending by sub-index, so they can be read back in order without sorting.
        for (int i = 0; i < changeCount; i++) SetPresent(newBitmap, changes.SubIndexAt(i), changes.Get(i) != default);

        int leafCount = PopCountRange(newBitmap, 0, LeafCount);
        if (leafCount == 0) return default;

        int groupCount = TwoLevelBitmapReader.OccupiedGroupsOf(newBitmap);
        RebuildState state = new(previousEntries, changes, 2 * leafCount - 1, groupCount, provider);
        state.Build(previousBitmap, newBitmap);
        return state;
    }

    /// <summary>Rewrites a legacy blob into the current format, dropping the single-child internal entries it no longer stores.</summary>
    /// <remarks>
    /// A filtering copy with no hashing: the current format's stored nodes are a subset of the legacy
    /// format's live nodes, both run in DFS post-order over the same leaf bitmap, and a branching internal's
    /// cached hash is identical either way. The footer carries over verbatim but for its format byte.
    /// </remarks>
    private static RefCountingMemory Upgrade(ReadOnlySpan<byte> blob, IRefCountingMemoryProvider provider)
    {
        TwoLevelBitmapReader reader = TwoLevelBitmapReader.FromBlob(blob, out ReadOnlySpan<byte> legacyEntries);
        Span<byte> bitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        reader.ExpandTo(bitmap);

        ReadOnlySpan<byte> footer = blob[legacyEntries.Length..];
        int storedCount = CountStoredNodes(bitmap, 0, LeafCount);
        RefCountingMemory memory = provider.Rent(storedCount * ValueLength + footer.Length);
        Span<byte> upgraded = memory.GetSpan();

        int legacySlot = 0;
        int slot = 0;
        CopyStoredNodes(bitmap, 0, LeafCount, legacyEntries, upgraded, ref legacySlot, ref slot);
        Debug.Assert(slot == storedCount);

        footer.CopyTo(upgraded[(storedCount * ValueLength)..]);
        upgraded[^1] = TwoLevelBitmapReader.FormatByte;
        return memory;
    }

    private static void CopyStoredNodes(
        scoped ReadOnlySpan<byte> bitmap,
        int low,
        int high,
        ReadOnlySpan<byte> legacyEntries,
        Span<byte> upgraded,
        ref int legacySlot,
        ref int slot)
    {
        if (!RangeHasLeaf(bitmap, low, high)) return;

        if (high - low == 1)
        {
            Copy(legacyEntries, legacySlot++, upgraded, slot++);
            return;
        }

        int middle = low + (high - low) / 2;
        CopyStoredNodes(bitmap, low, middle, legacyEntries, upgraded, ref legacySlot, ref slot);
        CopyStoredNodes(bitmap, middle, high, legacyEntries, upgraded, ref legacySlot, ref slot);

        // the legacy blob caches every live internal; keep only those that branch
        bool branching = RangeHasLeaf(bitmap, low, middle) && RangeHasLeaf(bitmap, middle, high);
        if (branching) Copy(legacyEntries, legacySlot, upgraded, slot++);
        legacySlot++;

        static void Copy(ReadOnlySpan<byte> source, int sourceSlot, Span<byte> destination, int destinationSlot) =>
            source.Slice(sourceSlot * ValueLength, ValueLength).CopyTo(destination.Slice(destinationSlot * ValueLength, ValueLength));
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

    /// <summary>The number of present leaves in <c>[low, high)</c>.</summary>
    /// <remarks>
    /// Deliberately not shared with <see cref="RangeHasLeaf"/>, which stops at the first present leaf and is
    /// called for every node of a rebuild; counting cannot stop early and would turn that into a full scan.
    /// </remarks>
    private static int PopCountRange(ReadOnlySpan<byte> bitmap, int low, int high)
    {
        int firstByte = low >> 3;
        int lastByte = (high - 1) >> 3;
        int count = 0;
        for (int byteIndex = firstByte; byteIndex <= lastByte; byteIndex++)
        {
            int from = byteIndex == firstByte ? low & 7 : 0;
            int to = byteIndex == lastByte && (high & 7) != 0 ? high & 7 : 8;
            byte mask = (byte)((0xff >> from) & (0xff << (8 - to)));
            count += BitOperations.PopCount((uint)(bitmap[byteIndex] & mask));
        }

        return count;
    }

    /// <summary>The number of nodes a rebuild emits for <paramref name="bitmap"/> over <c>[low, high)</c>: the present leaves plus the branching internals between them.</summary>
    /// <remarks>A binary tree with <c>m</c> leaves has exactly <c>m - 1</c> branching nodes, so the count is <c>2m - 1</c> however the leaves are placed.</remarks>
    private static int CountStoredNodes(ReadOnlySpan<byte> bitmap, int low, int high)
    {
        int leafCount = PopCountRange(bitmap, low, high);
        return leafCount == 0 ? 0 : 2 * leafCount - 1;
    }

    /// <summary>The number of nodes the legacy format holds for <paramref name="bitmap"/>: present leaves plus every internal node whose range holds one.</summary>
    private static int CountLiveNodes(ReadOnlySpan<byte> bitmap, int low, int high)
    {
        if (!RangeHasLeaf(bitmap, low, high)) return 0;
        if (high - low == 1) return 1;

        int middle = low + (high - low) / 2;
        return 1 + CountLiveNodes(bitmap, low, middle) + CountLiveNodes(bitmap, middle, high);
    }

    /// <summary>
    /// Accumulates into <paramref name="nodesBefore"/> the number of stored nodes whose post-order position
    /// precedes leaf <paramref name="target"/> — i.e. its slot in the post-order entry array. Post-order
    /// visits a subtree as <c>(left)&lt;(right)&lt;(root)</c>, so only whole subtrees entirely left of the
    /// target are counted wholesale; every enclosing internal node comes after the target and is skipped,
    /// which is why whether those ancestors are stored does not enter into it.
    /// </summary>
    /// <param name="legacy">Whether to count by the legacy layout, which also stores single-child internals.</param>
    private static void Locate(ReadOnlySpan<byte> bitmap, int low, int high, int target, bool legacy, ref int nodesBefore)
    {
        if (high - low == 1) return;

        int middle = low + (high - low) / 2;
        if (target < middle)
        {
            Locate(bitmap, low, middle, target, legacy, ref nodesBefore);
        }
        else
        {
            nodesBefore += legacy ? CountLiveNodes(bitmap, low, middle) : CountStoredNodes(bitmap, low, middle);
            Locate(bitmap, middle, high, target, legacy, ref nodesBefore);
        }
    }

    /// <remarks>
    /// Rebuilds directly into a final blob: the stored nodes fill the front in post-order, then the two-level
    /// bitmap footer is appended. The recursion hands each subtree's hash up by value, and the previous and
    /// new flat leaf bitmaps are threaded through as scoped parameters (they are the caller's stack buffers
    /// and must not escape). The buffer comes from an <see cref="IRefCountingMemoryProvider"/> and is
    /// released on <see cref="Dispose"/>; a <c>default</c> instance owns no buffer and represents the empty
    /// (no leaves remaining) result.
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
            int storedCount,
            int groupCount,
            IRefCountingMemoryProvider provider)
        {
            int length = storedCount * ValueLength
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
            SubtreeRoot = Rebuild(previousBitmap, newBitmap, 0, LeafCount);
            int footerLength = TwoLevelBitmapReader.Encode(newBitmap, _buffer[(_slot * ValueLength)..]);
            Debug.Assert(_slot * ValueLength + footerLength == _buffer.Length);
        }

        /// <summary>
        /// Hands the rebuilt blob's memory to the caller along with this state's lease on it, or
        /// <c>null</c> when <see cref="IsEmpty"/>. <see cref="Blob"/> must not be read afterwards.
        /// </summary>
        public RefCountingMemory? Take()
        {
            RefCountingMemory? memory = _memory;
            _memory = null;
            return memory;
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

        /// <summary>Rebuilds <c>[low, high)</c>, emitting its stored nodes, and returns its hash (<c>default</c> when the range holds no leaf).</summary>
        private ValueHash256 Rebuild(scoped ReadOnlySpan<byte> previousBitmap, scoped ReadOnlySpan<byte> newBitmap, int low, int high)
        {
            // changes arrive ascending and an earlier sibling consumed those below low, so the next one
            // lying at or past high proves none fall in this range
            bool clean = _changeIndex >= _changes.Count || _changes.SubIndexAt(_changeIndex) >= high;

            if (high - low == 1)
            {
                if (clean) return CopyCleanSubtree(previousBitmap, low, high);

                Debug.Assert(_changes.SubIndexAt(_changeIndex) == low);
                // _previousSlot walks the previous blob, so what it steps over turns on the previous bitmap
                if (IsPresent(previousBitmap, (byte)low)) _previousSlot++;

                ValueHash256 value = _changes.Get(_changeIndex++);
                if (value == default) return default;

                Append(value.Bytes);                    // a leaf entry holds the value; its parent needs the hash
                return Blake3Hash.Hash(value.Bytes);
            }

            int middle = low + (high - low) / 2;
            bool previousLeft = RangeHasLeaf(previousBitmap, low, middle);
            bool previousRight = RangeHasLeaf(previousBitmap, middle, high);

            // A single-child internal has no entry of its own, so its run does not end at its own hash and
            // CopyCleanSubtree could not return it. Recurse instead and let the copy fire at the branching
            // node (or leaf) below, where the run does end at the root. Equal flags mean branching or empty.
            if (clean && previousLeft == previousRight) return CopyCleanSubtree(previousBitmap, low, high);

            ValueHash256 left = Rebuild(previousBitmap, newBitmap, low, middle);
            ValueHash256 right = Rebuild(previousBitmap, newBitmap, middle, high);

            // post-order put the children's entries before this node's in the previous blob too, so its own
            // slot is only stepped over once they have been
            if (previousLeft && previousRight) _previousSlot++;

            ValueHash256 hash = Blake3Hash.HashPairOrZero(left, right);
            // What is emitted turns on the new bitmap where _previousSlot above turns on the previous one:
            // writing a leaf promotes a single-child internal to branching and clearing one demotes it, so
            // the two disagree at every ancestor of a change. Asking the bitmap rather than whether left and
            // right came back non-default keeps the popcount the buffer was sized from the only thing
            // deciding what lands in it.
            if (RangeHasLeaf(newBitmap, low, middle) && RangeHasLeaf(newBitmap, middle, high)) Append(hash.Bytes);
            return hash;   // returned whether or not it was stored: skipping withholds the entry, not the hash
        }

        /// <summary>Copies an unchanged subtree's stored nodes verbatim from the previous blob and returns its root's hash.</summary>
        /// <remarks>
        /// Their count is <see cref="CountStoredNodes"/> of the previous bitmap over the subtree — the same
        /// contiguous post-order run the previous rebuild emitted. The caller must exclude a single-child
        /// internal: only a stored root ends its own run, which is what makes the last copied entry the one
        /// to return. A clean subtree's bitmap is unchanged, so its node set cannot differ from the new one's.
        /// </remarks>
        private ValueHash256 CopyCleanSubtree(scoped ReadOnlySpan<byte> previousBitmap, int low, int high)
        {
            int count = CountStoredNodes(previousBitmap, low, high);
            if (count == 0) return default;

            _previousEntries.Slice(_previousSlot * ValueLength, count * ValueLength)
                .CopyTo(_buffer.Slice(_slot * ValueLength, count * ValueLength));
            _previousSlot += count;
            _slot += count;

            // a leaf entry holds its value, an internal entry its already-cached hash
            ReadOnlySpan<byte> root = NodeAt(_slot - 1);
            return high - low == 1 ? Blake3Hash.Hash(root) : new ValueHash256(root);
        }

        private void Append(ReadOnlySpan<byte> node)
        {
            node.CopyTo(_buffer.Slice(_slot * ValueLength, ValueLength));
            _slot++;
        }

        private readonly ReadOnlySpan<byte> NodeAt(int slot) => _buffer.Slice(slot * ValueLength, ValueLength);
    }
}
