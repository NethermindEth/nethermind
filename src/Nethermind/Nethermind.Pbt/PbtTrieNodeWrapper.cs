// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Collections;

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
/// Which groups wrap is fixed by <see cref="PbtLayout.IsWrappingDepth"/> — absolute depth, so that a run
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
        uint childSlotsBitmask = ChildSlotsBitmask(group);
        if ((childSlotsBitmask & bit) == 0) return default;

        int tableOffset = GroupOffset - BitOperations.PopCount(childSlotsBitmask) * OffsetLength;
        int index = BitOperations.PopCount(childSlotsBitmask & (bit - 1));
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
        uint childSlotsBitmask = ChildSlotsBitmask(group);
        int count = BitOperations.PopCount(childSlotsBitmask);
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
    private static uint ChildSlotsBitmask(in PbtTrieNodeGroup group) => group.BoundaryShape().ChildSlots;

    private static int GroupLength(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data[^TrailerLength..]);

    private int ChildEnd(int tableOffset, int index) => ReadOffset(_data, tableOffset, index);

    private static int ReadOffset(ReadOnlySpan<byte> data, int tableOffset, int index) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[(tableOffset + index * OffsetLength)..]);

    /// <summary>
    /// Writes a wrapper through a <see cref="BufferWriter"/>, in the order the encoding lays it out:
    /// the children in ascending slot order, then their offset table, then the group, then the
    /// trailer.
    /// </summary>
    /// <remarks>
    /// A child either arrives as bytes to copy (<see cref="AppendChild"/>) or is folded into the
    /// writer by the producer itself and then claimed (<see cref="MarkChild"/>) — the latter is what
    /// spares a rebuild a buffer per child and a copy out of it. Neither the count nor the group's
    /// length is needed until the table and the trailer respectively, so nothing has to be predicted
    /// before the children are in.
    /// <para>
    /// The builder carries the children's ends and nothing else: the writer is passed per call rather
    /// than held, a <c>ref</c> field being unable to point at a ref struct. Those ends are the writer's
    /// own count, which is the same thing because a wrapper is always the whole of the blob it is
    /// written into — <see cref="PbtLayout.IsWrappingDepth"/> alternates, so the group above one never holds it.
    /// </para>
    /// </remarks>
    public ref struct Builder
    {
        private RefList16<ushort> _ends;

        /// <summary>Appends one child's blob verbatim; children must be appended in ascending slot order.</summary>
        public void AppendChild(ref BufferWriter writer, ReadOnlySpan<byte> child)
        {
            writer.Write(child);
            MarkChild(in writer);
        }

        /// <summary>Claims everything written since the last child as one more of them.</summary>
        public void MarkChild(scoped in BufferWriter writer)
        {
            int end = writer.WrittenCount;
            Debug.Assert(end > (_ends.Count == 0 ? 0 : _ends[_ends.Count - 1]), "an absent child roots no blob to hold");
            _ends.Add((ushort)end);
        }

        /// <summary>Writes the offset table, which closes the children and precedes the group.</summary>
        public readonly void WriteOffsets(ref BufferWriter writer)
        {
            Debug.Assert(_ends.Count != 0, "a wrapper holds a group that roots at least one child blob");

            int length = _ends.Count * OffsetLength;
            Span<byte> table = writer.GetSpan(length);
            for (int index = 0; index < _ends.Count; index++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(table[(index * OffsetLength)..], _ends[index]);
            }

            writer.Advance(length);
        }

        /// <summary>Stamps the group's length and the format byte, closing the encoding.</summary>
        public readonly void Finish(ref BufferWriter writer, int groupLength)
        {
            Debug.Assert(_ends.Count != 0, "a wrapper holds a group that roots at least one child blob");

            Span<byte> trailer = writer.GetSpan(TrailerLength);
            BinaryPrimitives.WriteUInt16LittleEndian(trailer, (ushort)groupLength);
            trailer[sizeof(ushort)] = FormatByte;
            writer.Advance(TrailerLength);
        }
    }
}
