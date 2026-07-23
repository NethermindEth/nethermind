// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Which of its internal nodes a <see cref="StemLeafBlob"/> stores an entry for; the last byte of every non-empty blob.</summary>
/// <remarks>
/// All of them describe the same 256-leaf subtree and fold to the same root — they differ only in how
/// much of the fold they write down — so a store may hold any of them at any stem, and a blob converts
/// only when a change rewrites it.
/// </remarks>
public enum PbtLeafFormat : byte
{
    /// <summary>Every internal node whose range holds a leaf, the original layout; read but never written.</summary>
    Legacy = 0x01,

    /// <summary>Every branching internal node — one with a leaf in both halves — at every level.</summary>
    EveryLevel = 0x02,

    /// <summary>
    /// The branching internal nodes of the kept levels only, folding the skipped levels' hashes on
    /// demand; a stored node's stored children are its grandchildren. The subtree root is left out as
    /// well, the stem node holding the blob having cached it already.
    /// </summary>
    Interleaved = 0x03,

    /// <summary>
    /// The leaves alone: no internal node is stored, the whole subtree being folded from them on
    /// demand. A leaf holds a value rather than a hash, so nothing here is recomputable and nothing
    /// more can go.
    /// </summary>
    LeavesOnly = 0x04,

    /// <summary>
    /// One internal node every four depth — the branching nodes of the 16-wide level alone, the rest
    /// folded on demand. The leaves are stored whatever the format and the 256-wide root is left out as
    /// under the others, so this keeps a single mid-height level between them.
    /// </summary>
    Every4Depth = 0x05,
}

/// <summary>
/// The 256-leaf subtree of one stem, stored as its leaves and the internal nodes its format keeps in
/// DFS post-order, followed by a compact two-level leaf-presence bitmap and a format byte.
/// </summary>
/// <remarks>
/// The blob is <c>[ node entries: 32B × S ][ two-level bitmap footer ]</c>. Entries are in DFS post-order:
/// a leaf entry holds its 32-byte value, an internal entry holds its cached child pair-hash. A node's
/// presence and slot are implied entirely by the leaf bitmap, so no per-node offsets are stored. The
/// trailing footer is the two-level bitmap of <see cref="TwoLevelBitmapReader"/>. Zero values are
/// normalized to absent. An empty blob is an empty array (no footer) and signals stem deletion.
/// <para>
/// Which internal nodes earn an entry is <see cref="StoresInternal"/>, and it is a storage decision
/// alone — an omitted node is folded from its children wherever it is needed, so the subtree root is
/// the same under every format. Note that a single-child internal still hashes as
/// <c>HashPairOrZero(child, 0)</c>, which is not its child's hash.
/// </para>
/// <para>
/// <see cref="Apply"/> writes the format it is handed and reads any of them: a prior in another format
/// is refolded in full rather than copied over subtree by subtree, which is what converts it.
/// </para>
/// </remarks>
public static class StemLeafBlob
{
    public const int ValueLength = 32;
    private const int LeafCount = 256;

    /// <summary>Leaves per word of the flat bitmap, which <see cref="CountInterleavedNodes"/> reads it a word at a time.</summary>
    private const int LeavesPerWord = 64;

    public static bool TryGetValue(ReadOnlySpan<byte> blob, byte subIndex, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (blob.IsEmpty) return false;

        TwoLevelBitmapReader reader = TwoLevelBitmapReader.FromBlob(blob, out ReadOnlySpan<byte> entries);
        if (!reader.IsPresent(subIndex)) return false;

        Span<byte> bitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        reader.ExpandTo(bitmap);
        int slot = 0;
        Locate(bitmap, 0, LeafCount, subIndex, TwoLevelBitmapReader.FormatOf(blob), ref slot);
        value = entries.Slice(slot * ValueLength, ValueLength);
        return true;
    }

    /// <summary>Walks the present leaves of a <see cref="PbtLeafFormat.LeavesOnly"/> blob in ascending sub-index order.</summary>
    /// <remarks>
    /// Restricted to that one layout because it is the only one whose entries are the leaves alone: under
    /// every other, internal-node entries sit between them in post-order and a leaf's slot has to be
    /// located rather than counted. Bulk loads write leaves-only blobs (see <see cref="StemLeafBlobBuilder"/>),
    /// which is what reads them back.
    /// </remarks>
    /// <exception cref="InvalidDataException">The blob is in another layout.</exception>
    public static LeafEnumerator EnumerateLeavesOnly(ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty) return default;

        PbtLeafFormat format = TwoLevelBitmapReader.FormatOf(blob);
        if (format != PbtLeafFormat.LeavesOnly)
        {
            throw new InvalidDataException($"StemLeafBlob: a {format} blob interleaves internal nodes and cannot be enumerated as leaves");
        }

        return new LeafEnumerator(TwoLevelBitmapReader.FromBlob(blob, out ReadOnlySpan<byte> entries), entries);
    }

    /// <summary>The present leaves of a leaves-only blob, ascending by sub-index. See <see cref="EnumerateLeavesOnly"/>.</summary>
    public ref struct LeafEnumerator
    {
        private readonly TwoLevelBitmapReader _presence;
        private readonly ReadOnlySpan<byte> _entries;
        private int _subIndex;
        private int _slot;

        internal LeafEnumerator(TwoLevelBitmapReader presence, ReadOnlySpan<byte> entries)
        {
            _presence = presence;
            _entries = entries;
            _subIndex = -1;
            _slot = -1;
        }

        /// <summary>The sub-index of the leaf the enumerator sits on.</summary>
        public readonly byte CurrentSubIndex => (byte)_subIndex;

        /// <summary>The 32-byte value of the leaf the enumerator sits on.</summary>
        public readonly ReadOnlySpan<byte> CurrentValue => _entries.Slice(_slot * ValueLength, ValueLength);

        /// <remarks>A <c>default</c> enumerator holds no entries and stops at once, which is how an empty blob is walked.</remarks>
        public bool MoveNext()
        {
            while (++_subIndex < LeafCount)
            {
                if (!_presence.IsPresent((byte)_subIndex)) continue;

                _slot++;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Applies <paramref name="changes"/> (each a 32-byte leaf value; a zero value clears the leaf) to
    /// <paramref name="blob"/> in <paramref name="format"/>, returning a disposable <see cref="RebuildState"/>
    /// whose <see cref="RebuildState.Blob"/> is the new blob (empty, with <see cref="RebuildState.IsEmpty"/>
    /// set, when no leaves remain) and whose <see cref="RebuildState.SubtreeRoot"/> is the merkelized
    /// 256-leaf subtree root.
    /// </summary>
    /// <remarks>
    /// Rebuilds dirty paths in post-order and copies clean subtree entries verbatim. A present leaf
    /// contributes <c>blake3(value)</c>; higher levels use the EIP-8297 pair hash, with empty subtrees
    /// folding to zero. The blob is backed by memory from <paramref name="provider"/>, so the caller must
    /// dispose the result once its bytes have been consumed.
    /// <para>
    /// <paramref name="blob"/> may be in any format: it is read through its own, and where that is not
    /// the one being written no subtree of it is clean, so the whole stem refolds and converts.
    /// </para>
    /// </remarks>
    internal static RebuildState Apply(
        ReadOnlySpan<byte> blob, IPbtStemChanges changes, IRefCountingMemoryProvider provider, PbtLeafFormat format)
    {
        Span<byte> previousBitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        ReadOnlySpan<byte> previousEntries = default;
        // an empty prior has no entries to step over, so its format is only ever this one
        PbtLeafFormat previousFormat = format;
        if (blob.IsEmpty)
        {
            previousBitmap.Clear();
        }
        else
        {
            TwoLevelBitmapReader.FromBlob(blob, out previousEntries).ExpandTo(previousBitmap);
            previousFormat = TwoLevelBitmapReader.FormatOf(blob);
        }

        Span<byte> newBitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        previousBitmap.CopyTo(newBitmap);

        int changeCount = changes.Count;
        // The map keeps writes ascending by sub-index, so they can be read back in order without sorting.
        for (int i = 0; i < changeCount; i++) SetPresent(newBitmap, changes.SubIndexAt(i), changes.Get(i) != default);

        int leafCount = PopCountRange(newBitmap, 0, LeafCount);
        if (leafCount == 0) return default;

        int groupCount = TwoLevelBitmapReader.OccupiedGroupsOf(newBitmap);
        // the leaves alone are the whole of a LeavesOnly blob, so there its bound is its exact size
        int storedCountBound = format == PbtLeafFormat.LeavesOnly ? leafCount : 2 * leafCount - 1;
        RebuildState state = new(previousEntries, previousFormat, changes, storedCountBound, groupCount, provider, format);
        state.Build(previousBitmap, newBitmap);
        return state;
    }

    /// <summary>
    /// Whether <paramref name="format"/> stores an entry for the internal node covering
    /// <paramref name="width"/> leaves, given whether each of its halves holds one.
    /// </summary>
    /// <remarks>
    /// A single-child internal is left out of every written format: it is recomputed from the child
    /// below it, which is one hash away, where a branching node's would cost a walk of both its halves.
    /// <see cref="PbtLeafFormat.Interleaved"/> leaves out whole levels on top of that, and
    /// <see cref="PbtLeafFormat.LeavesOnly"/> all of them.
    /// </remarks>
    private static bool StoresInternal(PbtLeafFormat format, int width, bool leftHasLeaf, bool rightHasLeaf) =>
        format == PbtLeafFormat.Legacy
            ? leftHasLeaf || rightHasLeaf
            : leftHasLeaf && rightHasLeaf && PbtLayout.StemLeafStoresInternalAtWidth(format, width);

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

    /// <summary>The number of entries <paramref name="format"/> holds for <paramref name="bitmap"/> over the aligned range <c>[low, high)</c>: the present leaves plus the internal nodes it stores between them.</summary>
    /// <remarks>
    /// Counted rather than walked, this being on the path of every read and of every clean subtree a
    /// rebuild copies: a walk of the nodes costs more than the copy it is sizing.
    /// <see cref="PbtLeafFormat.EveryLevel"/> has a closed form — a binary tree with <c>m</c> leaves has
    /// exactly <c>m - 1</c> branching nodes, so the count is <c>2m - 1</c> however the leaves are placed
    /// — as does <see cref="PbtLeafFormat.LeavesOnly"/>, whose entries are the leaves themselves, while
    /// <see cref="PbtLeafFormat.Interleaved"/> and <see cref="PbtLeafFormat.Every4Depth"/>, whose closed
    /// forms are a bit fold, are counted by <see cref="CountInterleavedNodes"/> and
    /// <see cref="CountEvery4Nodes"/> instead. Only the legacy layout, which nothing writes, is walked.
    /// </remarks>
    internal static int CountStoredNodes(ReadOnlySpan<byte> bitmap, int low, int high, PbtLeafFormat format)
    {
        if (format == PbtLeafFormat.LeavesOnly) return PopCountRange(bitmap, low, high);

        if (format == PbtLeafFormat.EveryLevel)
        {
            int leafCount = PopCountRange(bitmap, low, high);
            return leafCount == 0 ? 0 : 2 * leafCount - 1;
        }

        if (format == PbtLeafFormat.Interleaved) return CountInterleavedNodes(bitmap, low, high);
        if (format == PbtLeafFormat.Every4Depth) return CountEvery4Nodes(bitmap, low, high);

        if (!RangeHasLeaf(bitmap, low, high)) return 0;
        if (high - low == 1) return 1;

        int middle = low + (high - low) / 2;
        return 1 + CountStoredNodes(bitmap, low, middle, format) + CountStoredNodes(bitmap, middle, high, format);
    }

    /// <summary>
    /// <see cref="CountStoredNodes"/> for <see cref="PbtLeafFormat.Interleaved"/>: the leaves of the
    /// aligned range <c>[low, high)</c> plus the branching nodes of its 4-, 16- and 64-wide levels.
    /// </summary>
    /// <remarks>
    /// A level's nodes are aligned blocks of the leaf bitmap, so the whole level is settled at once:
    /// folding each block's bits down to one says which blocks hold a leaf, and <c>and</c>-ing that
    /// against itself shifted by half a block says which hold one in both halves — a branching node. All
    /// three kept levels sit inside a 64-leaf word, so no block straddles two, and a range narrower than
    /// one is handled by masking the word down to it: a block reaching outside then has an empty half
    /// and drops out of its own accord. The levels this layout skips are simply never asked about, and
    /// the 128- and 256-wide ones are among them.
    /// </remarks>
    private static int CountInterleavedNodes(ReadOnlySpan<byte> bitmap, int low, int high)
    {
        int width = high - low;
        ulong rangeMask = width < LeavesPerWord
            ? (ulong.MaxValue << (LeavesPerWord - width)) >> (low & (LeavesPerWord - 1))
            : ulong.MaxValue;

        int count = 0;
        for (int word = low / LeavesPerWord; word <= (high - 1) / LeavesPerWord; word++)
        {
            ulong leaves = BinaryPrimitives.ReadUInt64BigEndian(bitmap.Slice(word * sizeof(ulong), sizeof(ulong))) & rangeMask;
            if (leaves == 0) continue;

            // One bit per block, at the block's first leaf, which the bitmap being MSB-first puts at the
            // block's highest bit — so a block folds by shifting its second half up onto its first.
            // 0xAA… marks every second bit, 0x88… every fourth, and so on.
            ulong pairs = (leaves | (leaves << 1)) & 0xAAAA_AAAA_AAAA_AAAAul;
            ulong quads = (pairs | (pairs << 2)) & 0x8888_8888_8888_8888ul;
            ulong eights = (quads | (quads << 4)) & 0x8080_8080_8080_8080ul;
            ulong sixteens = (eights | (eights << 8)) & 0x8000_8000_8000_8000ul;
            ulong thirtyTwos = (sixteens | (sixteens << 16)) & 0x8000_0000_8000_0000ul;

            count += BitOperations.PopCount(leaves)
                + BitOperations.PopCount(pairs & (pairs << 2) & 0x8888_8888_8888_8888ul)
                + BitOperations.PopCount(eights & (eights << 8) & 0x8000_8000_8000_8000ul)
                + BitOperations.PopCount(thirtyTwos & (thirtyTwos << 32) & 0x8000_0000_0000_0000ul);
        }

        return count;
    }

    /// <summary>
    /// <see cref="CountStoredNodes"/> for <see cref="PbtLeafFormat.Every4Depth"/>: the leaves of the
    /// aligned range <c>[low, high)</c> plus the branching nodes of its 16-wide level alone.
    /// </summary>
    /// <remarks>
    /// The 16-wide branching term of <see cref="CountInterleavedNodes"/> on its own — its every-2-depth
    /// fold with the 4- and 64-wide levels dropped — sharing that method's word loop and range mask, a
    /// 16-wide block sitting inside a 64-leaf word so none straddles two.
    /// </remarks>
    private static int CountEvery4Nodes(ReadOnlySpan<byte> bitmap, int low, int high)
    {
        int width = high - low;
        ulong rangeMask = width < LeavesPerWord
            ? (ulong.MaxValue << (LeavesPerWord - width)) >> (low & (LeavesPerWord - 1))
            : ulong.MaxValue;

        int count = 0;
        for (int word = low / LeavesPerWord; word <= (high - 1) / LeavesPerWord; word++)
        {
            ulong leaves = BinaryPrimitives.ReadUInt64BigEndian(bitmap.Slice(word * sizeof(ulong), sizeof(ulong))) & rangeMask;
            if (leaves == 0) continue;

            ulong pairs = (leaves | (leaves << 1)) & 0xAAAA_AAAA_AAAA_AAAAul;
            ulong quads = (pairs | (pairs << 2)) & 0x8888_8888_8888_8888ul;
            ulong eights = (quads | (quads << 4)) & 0x8080_8080_8080_8080ul;

            count += BitOperations.PopCount(leaves)
                + BitOperations.PopCount(eights & (eights << 8) & 0x8000_8000_8000_8000ul);
        }

        return count;
    }

    /// <summary>
    /// Accumulates into <paramref name="nodesBefore"/> the number of stored nodes whose post-order position
    /// precedes leaf <paramref name="target"/> — i.e. its slot in the post-order entry array. Post-order
    /// visits a subtree as <c>(left)&lt;(right)&lt;(root)</c>, so only whole subtrees entirely left of the
    /// target are counted wholesale; every enclosing internal node comes after the target and is skipped,
    /// which is why whether those ancestors are stored does not enter into it.
    /// </summary>
    /// <param name="format">The layout the blob being read holds its entries in.</param>
    private static void Locate(ReadOnlySpan<byte> bitmap, int low, int high, int target, PbtLeafFormat format, ref int nodesBefore)
    {
        if (high - low == 1) return;

        int middle = low + (high - low) / 2;
        if (target < middle)
        {
            Locate(bitmap, low, middle, target, format, ref nodesBefore);
        }
        else
        {
            nodesBefore += CountStoredNodes(bitmap, low, middle, format);
            Locate(bitmap, middle, high, target, format, ref nodesBefore);
        }
    }

    /// <remarks>
    /// Rebuilds directly into a final blob: the stored nodes fill the front in post-order, then the two-level
    /// bitmap footer is appended. The recursion hands each subtree's hash up by value, and the previous and
    /// new flat leaf bitmaps are threaded through as scoped parameters (they are the caller's stack buffers
    /// and must not escape). The buffer comes from an <see cref="IRefCountingMemoryProvider"/> and is
    /// released on <see cref="Dispose"/>; a <c>default</c> instance owns no buffer and represents the empty
    /// (no leaves remaining) result.
    /// <para>
    /// It is rented to the bound of what the leaves can cost and narrowed to what they did once the fold
    /// is done, there being no counting the entries of a format that leaves out levels without walking
    /// the whole subtree first.
    /// </para>
    /// </remarks>
    internal ref struct RebuildState
    {
        private readonly ReadOnlySpan<byte> _previousEntries;
        private readonly PbtLeafFormat _previousFormat;
        private readonly PbtLeafFormat _format;
        private readonly IPbtStemChanges _changes;
        private readonly Span<byte> _buffer;
        private RefCountingMemory? _memory;
        private int _previousSlot;
        private int _changeIndex;
        private int _slot;
        private int _length;

        /// <summary>The rebuilt blob, valid until <see cref="Dispose"/>; empty when <see cref="IsEmpty"/>.</summary>
        public readonly ReadOnlySpan<byte> Blob => _buffer[.._length];

        /// <summary>Whether the stem has no leaves remaining, in which case <see cref="Blob"/> is empty.</summary>
        public readonly bool IsEmpty => _memory is null;

        /// <summary>The merkelized 256-leaf subtree root, populated by <see cref="Build"/>.</summary>
        public ValueHash256 SubtreeRoot { get; private set; }

        public RebuildState(
            ReadOnlySpan<byte> previousEntries,
            PbtLeafFormat previousFormat,
            IPbtStemChanges changes,
            int storedCountBound,
            int groupCount,
            IRefCountingMemoryProvider provider,
            PbtLeafFormat format)
        {
            int length = storedCountBound * ValueLength
                + groupCount * TwoLevelBitmapReader.SubWordLength
                + TwoLevelBitmapReader.TopLength
                + TwoLevelBitmapReader.FormatLength;
            _memory = provider.Rent(length);
            _buffer = _memory.GetSpan();

            _previousEntries = previousEntries;
            _previousFormat = previousFormat;
            _format = format;
            _changes = changes;
            _previousSlot = 0;
            _changeIndex = 0;
            _slot = 0;
        }

        /// <summary>Runs the post-order rebuild into <see cref="Blob"/>, sets <see cref="SubtreeRoot"/>, and appends the footer.</summary>
        public void Build(scoped ReadOnlySpan<byte> previousBitmap, scoped ReadOnlySpan<byte> newBitmap)
        {
            SubtreeRoot = Rebuild(previousBitmap, newBitmap, 0, LeafCount);
            int footerLength = TwoLevelBitmapReader.Encode(newBitmap, _buffer[(_slot * ValueLength)..], _format);
            _length = _slot * ValueLength + footerLength;
            Debug.Assert(_length <= _buffer.Length);
            _memory!.Shrink(_length);
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

            // A node the previous blob stored no entry for does not end its own post-order run, so
            // CopyCleanSubtree could not return its hash. Recurse instead and let the copy fire at the
            // stored node (or leaf) below, where the run does end at the root — which also refolds a prior
            // in another format, whose runs are not this format's to splice in. An empty range is copied
            // whatever the format: there is nothing to copy and nothing to fold.
            if (clean
                && (!previousLeft && !previousRight
                    || _previousFormat == _format && StoresInternal(_format, high - low, previousLeft, previousRight)))
            {
                return CopyCleanSubtree(previousBitmap, low, high);
            }

            ValueHash256 left = Rebuild(previousBitmap, newBitmap, low, middle);
            ValueHash256 right = Rebuild(previousBitmap, newBitmap, middle, high);

            // post-order put the children's entries before this node's in the previous blob too, so its own
            // slot is only stepped over once they have been — and by that blob's format, not this one
            if (StoresInternal(_previousFormat, high - low, previousLeft, previousRight)) _previousSlot++;

            ValueHash256 hash = Blake3Hash.HashPairOrZero(left, right);
            // What is emitted turns on the new bitmap where _previousSlot above turns on the previous one:
            // writing a leaf promotes a single-child internal to branching and clearing one demotes it, so
            // the two disagree at every ancestor of a change. Asking the bitmap rather than whether left and
            // right came back non-default keeps the popcount the buffer was sized from the only thing
            // deciding what lands in it.
            if (StoresInternal(_format, high - low, RangeHasLeaf(newBitmap, low, middle), RangeHasLeaf(newBitmap, middle, high)))
            {
                Append(hash.Bytes);
            }

            return hash;   // returned whether or not it was stored: skipping withholds the entry, not the hash
        }

        /// <summary>Copies an unchanged subtree's stored nodes verbatim from the previous blob and returns its root's hash.</summary>
        /// <remarks>
        /// Their count is <see cref="CountStoredNodes"/> of the previous bitmap over the subtree — the same
        /// contiguous post-order run the previous rebuild emitted, and so counted by that blob's format.
        /// The caller must exclude a root that blob stored no entry for: only a stored root ends its own
        /// run, which is what makes the last copied entry the one to return. A clean subtree's bitmap is
        /// unchanged, so its node set cannot differ from the new one's.
        /// </remarks>
        private ValueHash256 CopyCleanSubtree(scoped ReadOnlySpan<byte> previousBitmap, int low, int high)
        {
            int count = CountStoredNodes(previousBitmap, low, high, _previousFormat);
            if (count == 0) return default;

            _previousEntries.Slice(_previousSlot * ValueLength, count * ValueLength)
                .CopyTo(_buffer.Slice(_slot * ValueLength, count * ValueLength));
            _previousSlot += count;
            _slot += count;

            // a leaf entry holds its value, an internal entry its already-cached hash
            ReadOnlySpan<byte> root = _buffer.Slice((_slot - 1) * ValueLength, ValueLength);
            return high - low == 1 ? Blake3Hash.Hash(root) : new ValueHash256(root);
        }

        private void Append(ReadOnlySpan<byte> node)
        {
            node.CopyTo(_buffer.Slice(_slot * ValueLength, ValueLength));
            _slot++;
        }
    }
}
