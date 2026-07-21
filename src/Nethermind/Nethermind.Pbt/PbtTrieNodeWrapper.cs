// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>
/// One blob holding a <see cref="PbtTrieNodeGroup"/> together with the blobs of all of its child
/// groups, so that a single store lookup serves eight trie levels rather than four.
/// </summary>
/// <remarks>
/// A group's worst case is 655 bytes — far below an SSD block, let alone a RocksDB one — so a lookup
/// that buys four levels wastes most of the block it reads. Widening the group itself is what this
/// avoids: the descent radix-partitions each range into the group's sixteen boundary slots
/// (<c>TrieUpdater.Partition</c>), and that bucket sort already carries a fixed sixteen-slot bounds
/// array per frame for ranges that are usually one or two entries; 256 buckets would multiply that
/// overhead on exactly the sparse frames that dominate. So the tile stays four levels for the
/// descent, and only the unit of storage grows.
/// <para>
/// The encoding is the child blobs in ascending boundary-slot order, one little-endian 16-bit end
/// offset per child, then the group's own encoding, its length, and the format byte. Each child is a
/// complete group encoding, and child <c>i</c> runs from the previous end offset (0 for the first) to
/// its own, the last of them ending where the offsets begin. Children lead because the fold produces
/// them first: a rebuild settles every child before the hashes reach the group above them. In the
/// sparse subtrees that dominate, a few hundred bytes.
/// </para>
/// <para>
/// The trailing group length is what makes the whole layout readable backwards from the blob alone:
/// it pins the group, whose bitmaps then say how many children there are — a boundary slot roots a
/// blob exactly where it holds an internal node — which pins the offsets, which pin the children. So
/// neither a count nor a slot mask is stored, and a view of one is the bytes and nothing else.
/// </para>
/// <para>
/// Which groups wrap is fixed by <see cref="WrapsChildren"/> — absolute depth, so that a run
/// appearing or splitting somewhere above never re-parities the subtree below it, and so that the
/// wrapped level is always the level that does not itself wrap: a child pulled into a wrapper or
/// pushed back out of one is a bare group, never another wrapper.
/// </para>
/// </remarks>
public readonly ref struct PbtTrieNodeWrapper(ReadOnlySpan<byte> data)
{
    /// <summary>Version sentinel ending every encoding; distinct from those of the two blob formats it holds.</summary>
    private const byte FormatByte = 0x04;

    private const int OffsetLength = sizeof(ushort);

    /// <summary>The group length and the format byte closing every encoding; the length sits at <c>data[^TrailerLength..]</c>.</summary>
    private const int TrailerLength = sizeof(ushort) + sizeof(byte);

    private readonly ReadOnlySpan<byte> _data = data;

    /// <summary>
    /// Whether the group at <paramref name="depth"/> holds its children's blobs inside its own, which
    /// alternates by group so that every other level is reached without a lookup of its own.
    /// </summary>
    /// <remarks>
    /// Absolute rather than relative to the root or to the nearest run, which is what keeps a blob's
    /// placement a function of its depth alone: a run splitting at an intermediate depth would
    /// otherwise flip the parity of everything below it, re-keying a whole subtree. Depth 0 does not
    /// wrap, so the zone roots at depth 4 keep the keys their columns are routed by.
    /// </remarks>
    public static bool WrapsChildren(int depth) => (depth & PbtTrieNodeGroup.LevelsPerGroup) != 0;

    /// <summary>True for an encoding ending in this type's format byte rather than a group's.</summary>
    public static bool IsWrapper(ReadOnlySpan<byte> data) => data.Length > 0 && data[^1] == FormatByte;

    /// <summary>The length of a wrapper of <paramref name="childCount"/> children totalling <paramref name="childBytes"/> over a group of <paramref name="groupLength"/> bytes.</summary>
    public static int EncodedLength(int childBytes, int groupLength, int childCount) =>
        childBytes + childCount * OffsetLength + groupLength + TrailerLength;

    /// <summary>True where no child is held: a bare group blob, or the absent blob.</summary>
    public bool IsEmpty => !IsWrapper(_data);

    /// <summary>Where the group's own encoding sits in the blob, which is the whole of a bare one.</summary>
    public Range Group => IsEmpty ? 0.._data.Length : GroupOffset..^TrailerLength;

    /// <summary>
    /// Where the group's own encoding starts, which for a bare group blob is zero. Entry offsets into
    /// the group are relative to it, so a consumer holding one adds this.
    /// </summary>
    public int GroupOffset => IsEmpty ? 0 : _data.Length - TrailerLength - GroupLength(_data);

    /// <summary>
    /// Where the blob of the child group under boundary slot <paramref name="slot"/> sits, empty where
    /// that slot roots none — as it always is for a bare group, whose children are stored under their
    /// own keys.
    /// </summary>
    /// <remarks>
    /// A range rather than the bytes, because a consumer holding the blob in a larger buffer — the
    /// descent, which reads it out of the memory the store handed over — needs where it starts as much
    /// as what it says.
    /// </remarks>
    /// <param name="group">The group this wraps, whose bitmaps say which slots root a blob and how many do.</param>
    public Range Child(int slot, in PbtTrieNodeGroup group)
    {
        if (IsEmpty) return default;

        uint bit = 1u << slot;
        uint childSlots = ChildSlots(group);
        if ((childSlots & bit) == 0) return default;

        int tableOffset = GroupOffset - BitOperations.PopCount(childSlots) * OffsetLength;
        int index = BitOperations.PopCount(childSlots & (bit - 1));
        return (index == 0 ? 0 : ChildEnd(tableOffset, index - 1))..ChildEnd(tableOffset, index);
    }

    /// <summary>
    /// Reads a stored blob as the group it holds and, where it wraps them, that group's children; a
    /// bare group blob reads as a wrapper holding none, whose <see cref="Group"/> is the whole blob.
    /// </summary>
    /// <param name="group">The decoded group, which either way borrows <paramref name="data"/>.</param>
    /// <exception cref="InvalidDataException">The blob is not a well-formed wrapper or group.</exception>
    public static PbtTrieNodeWrapper Decode(ReadOnlySpan<byte> data, out PbtTrieNodeGroup group)
    {
        PbtTrieNodeWrapper wrapper = new(data);
        if (wrapper.IsEmpty)
        {
            group = PbtTrieNodeGroup.Decode(data);
            return wrapper;
        }

        if (data.Length <= TrailerLength) throw new InvalidDataException($"Trie node wrapper too short: {data.Length} bytes");

        // the trailing length pins the group, whose bitmaps then say how many children there are and
        // which slots they belong to — so everything below it follows from the group
        int groupOffset = data.Length - TrailerLength - GroupLength(data);
        if (groupOffset <= 0) throw new InvalidDataException($"Trie node wrapper of {data.Length} bytes says its group is {GroupLength(data)}");

        group = PbtTrieNodeGroup.Decode(data[groupOffset..^TrailerLength]);
        uint childSlots = ChildSlots(group);
        int count = BitOperations.PopCount(childSlots);
        if (count == 0) throw new InvalidDataException("Trie node wrapper holds a group that roots no child blob");

        int tableOffset = groupOffset - count * OffsetLength;
        if (tableOffset <= 0) throw new InvalidDataException($"Trie node wrapper of {data.Length} bytes leaves no room for {count} children");

        // The offsets partition the bytes ahead of them, so they ascend and the last ends exactly where
        // they begin. None may be empty: an empty group encodes to zero bytes, which is the store's
        // removal marker rather than a blob.
        int end = 0;
        for (int index = 0; index < count; index++)
        {
            int childEnd = ReadOffset(data, tableOffset, index);
            if (childEnd <= end || childEnd > tableOffset) throw new InvalidDataException($"Trie node wrapper child {index} ends at {childEnd}");
            end = childEnd;
        }

        if (end != tableOffset) throw new InvalidDataException($"Trie node wrapper children end at {end}, not {tableOffset}");

        return wrapper;
    }

    /// <summary>
    /// The boundary slots rooting a blob of their own: a group's boundary internals, exactly. A slot
    /// holding a stem or a run roots none — both live in the group's own encoding.
    /// </summary>
    private static uint ChildSlots(in PbtTrieNodeGroup group) => group.BoundaryShape().ChildSlots;

    private static int GroupLength(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data[^TrailerLength..]);

    private int ChildEnd(int tableOffset, int index) => ReadOffset(_data, tableOffset, index);

    private static int ReadOffset(ReadOnlySpan<byte> data, int tableOffset, int index) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[(tableOffset + index * OffsetLength)..]);

    /// <summary>
    /// Writes a wrapper into a caller-supplied buffer of exactly <see cref="EncodedLength"/> bytes:
    /// the children first, in ascending slot order, then the group folded straight into
    /// <see cref="GroupDestination"/>, then the trailer.
    /// </summary>
    /// <remarks>
    /// The group's length has to be known before the buffer is rented, which the shape prediction that
    /// already sizes a bare group's encoding gives — and knowing it is also what places the offset
    /// table, so the children's ends are written where they fall rather than backfilled.
    /// </remarks>
    public ref struct Builder(Span<byte> destination, int childCount, int groupLength)
    {
        private readonly Span<byte> _destination = destination;
        private readonly int _childCount = childCount;
        private readonly int _tableOffset = destination.Length - TrailerLength - groupLength - childCount * OffsetLength;
        private int _index;
        private int _offset;

        /// <summary>Appends one child's blob verbatim; children must be appended in ascending slot order.</summary>
        public void AppendChild(ReadOnlySpan<byte> child)
        {
            Debug.Assert(!child.IsEmpty, "an absent child roots no blob to hold");
            Debug.Assert(_index < _childCount, "more children than the wrapper was sized for");

            child.CopyTo(_destination[_offset..]);
            _offset += child.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(_destination[(_tableOffset + _index * OffsetLength)..], (ushort)_offset);
            _index++;
        }

        /// <summary>Where the group's own encoding goes, once every child is appended.</summary>
        public readonly Span<byte> GroupDestination
        {
            get
            {
                Debug.Assert(_index == _childCount && _offset == _tableOffset, "the offsets follow every child, and the group those");
                return _destination[(_tableOffset + _childCount * OffsetLength)..^TrailerLength];
            }
        }

        /// <summary>Stamps the group's length and the format byte, and returns the encoded length.</summary>
        public readonly int Finish()
        {
            Debug.Assert(_index == _childCount, "the wrapper holds fewer children than it was sized for");

            BinaryPrimitives.WriteUInt16LittleEndian(_destination[^TrailerLength..], (ushort)GroupDestination.Length);
            _destination[^1] = FormatByte;
            return _destination.Length;
        }
    }
}
