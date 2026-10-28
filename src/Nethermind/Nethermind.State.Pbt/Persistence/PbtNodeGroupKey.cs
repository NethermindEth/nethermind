// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Persisted node-group key.</summary>
/// <remarks>
/// The group path bytes, then a trailer byte: the number of path bits in the last byte, 0 when the path fills it. Keys
/// carry no padding, so a subtree is still one contiguous key range, but the trailer is compared against the next path
/// byte of longer keys. A byte-aligned group's 0 never exceeds that byte, and on a tie the shorter key wins, so it sorts
/// at the front of its subtree; any other group's trailer sorts it after the descendants whose next byte is below it,
/// inside its subtree's range rather than at its front.
/// </remarks>
public static class PbtNodeGroupKey
{
    private const int TrailerLength = 1;
    public const int MaxLength = PbtVariableTreeKey.MaxLength + TrailerLength;

    public static ReadOnlySpan<byte> Encode<TPath>(TPath groupKey, Span<byte> destination)
        where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> key = destination[..(PbtBitPrefix.ByteCount(groupKey.BitDepth) + TrailerLength)];
        PbtNodePathOperations.CopyTo(groupKey, key);
        key[^1] = (byte)(groupKey.BitDepth & 7);
        return key;
    }

    public static PbtStorageNodePath Decode(ReadOnlySpan<byte> key)
    {
        // The root group lives under its own metadata key, never in a node-group column.
        if (key.Length < 1 + TrailerLength || key.Length - TrailerLength > PbtVariableTreeKey.MaxLength)
            throw new InvalidDataException("Invalid persisted PBT node-group key length.");
        if (key[^1] > 7) throw new InvalidDataException("Invalid persisted PBT node-group key trailer.");
        int bitsInLastByte = key[^1] == 0 ? 8 : key[^1];
        int depth = (key.Length - TrailerLength - 1) * 8 + bitsInLastByte;
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(depth))
            throw new InvalidDataException("A persisted PBT node-group key depth must be a group boundary.");
        ReadOnlySpan<byte> path = key[..^TrailerLength];
        if (!PbtNodeCodec.IsCanonicalPath(path, depth, PbtStorageNodePath.MaxBitDepth))
            throw new InvalidDataException("Invalid persisted PBT node-group key padding.");
        return PbtStorageNodePath.Create(path, depth);
    }

    /// <summary>Enumerates the groups of the tree under <paramref name="rootPointer"/> reserved at or after <paramref name="lowerBound"/>: the root group first, then the rest in key order.</summary>
    /// <remarks>
    /// The root group is keyed apart from the others. Below it, a linked group's subtree is one contiguous key range whose
    /// prefix is the group's path, and the ranges of a group's links follow their slot order. A group sorts before its
    /// subtree or inside one of its children's ranges, so it is carried down as pending until the walk passes it. Links below
    /// <paramref name="lowerBound"/>, and links not in the buffer, are not followed. Returned records are borrowed: a pin at
    /// or below <paramref name="lowerBound"/> must outlive the enumeration and every use of the records.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A record at or above <paramref name="lowerBound"/> is no longer resident.</exception>
    public static KeyOrderEnumerable InKeyOrder(IPbtGroupRecordBuffer buffer, ulong rootPointer, ulong lowerBound) => new(buffer, rootPointer, lowerBound);

    private static bool TryResolve(IPbtGroupRecordBuffer buffer, ulong pointer, ulong lowerBound, out PbtGroupRecord record)
    {
        record = default;
        if (pointer == 0 || pointer < lowerBound) return false;
        if (!buffer.TryLocate(pointer, out record)) throw new InvalidOperationException("A group record written since the lower bound is no longer resident.");
        return true;
    }

    /// <param name="key">An encoded group key.</param>
    /// <param name="prefix">The <see cref="Encode{TPath}"/> encoding of the prefix path.</param>
    /// <param name="prefixBitDepth">The bit depth of the prefix path.</param>
    /// <returns>Negative or positive when <paramref name="key"/> sorts before or after every key starting with the bits of <paramref name="prefix"/>, or zero when it starts with them.</returns>
    private static int CompareToRange(ReadOnlySpan<byte> key, ReadOnlySpan<byte> prefix, int prefixBitDepth)
    {
        int fullBytes = prefixBitDepth >> 3;
        int order = key[..Math.Min(key.Length, fullBytes)].SequenceCompareTo(prefix[..Math.Min(key.Length, fullBytes)]);
        if (order != 0) return order;
        if (key.Length < fullBytes) return -1;
        int remainder = prefixBitDepth & 7;
        if (remainder == 0) return 0;
        if (key.Length == fullBytes) return -1;
        int mask = 0xFF << (8 - remainder) & 0xFF;
        return (key[fullBytes] & mask) - (prefix[fullBytes] & mask);
    }

    public readonly ref struct KeyOrderEnumerable(IPbtGroupRecordBuffer buffer, ulong rootPointer, ulong lowerBound)
    {
        private readonly IPbtGroupRecordBuffer _buffer = buffer;
        private readonly ulong _rootPointer = rootPointer;
        private readonly ulong _lowerBound = lowerBound;

        public KeyOrderEnumerator GetEnumerator() => new(_buffer, _rootPointer, _lowerBound);
    }

    /// <remarks>
    /// Pending groups are kept in descending key order, so the next one to emit is the last, and the pending groups in a
    /// child's range are a tail of its parent's, which the child's frame emits before it is popped.
    /// </remarks>
    public ref struct KeyOrderEnumerator(IPbtGroupRecordBuffer buffer, ulong rootPointer, ulong lowerBound) : IDisposable
    {
        private readonly IPbtGroupRecordBuffer _buffer = buffer;
        private readonly ulong _rootPointer = rootPointer;
        private readonly ulong _lowerBound = lowerBound;
        private ArrayPoolListRef<Frame> _stack = new(4);
        private ArrayPoolListRef<PbtGroupRecord> _pending = new(4);
        private bool _started;

        public PbtGroupRecord Current { get; private set; }

        public bool MoveNext()
        {
            if (!_started)
            {
                _started = true;
                if (TryResolve(_buffer, _rootPointer, _lowerBound, out PbtGroupRecord root))
                {
                    _stack.Add(new Frame { Record = root });
                    Current = root;
                    return true;
                }
            }

            Span<byte> key = stackalloc byte[MaxLength];
            Span<byte> childKey = stackalloc byte[MaxLength];
            while (_stack.Count > 0)
            {
                ref Frame frame = ref _stack.GetRef(_stack.Count - 1);
                if (frame.HasChild)
                {
                    ReadOnlySpan<byte> encodedChild = Encode(frame.Child.Path, childKey);
                    int childBitDepth = frame.Child.Path.BitDepth;
                    if (_pending.Count > frame.PendingStart && CompareToRange(Encode(_pending[^1].Path, key), encodedChild, childBitDepth) < 0) return EmitLast();

                    int inside = _pending.Count;
                    while (inside > frame.PendingStart && CompareToRange(Encode(_pending[inside - 1].Path, key), encodedChild, childBitDepth) == 0) inside--;
                    int index = _pending.Count;
                    while (index > inside && Encode(_pending[index - 1].Path, key).SequenceCompareTo(encodedChild) < 0) index--;
                    PbtGroupRecord child = frame.Child;
                    frame.HasChild = false;
                    _pending.Insert(index, child);
                    _stack.Add(new Frame { Record = child, PendingStart = inside });
                    continue;
                }

                if (frame.NextSlot < PbtFourLevelGroupGeometry.BoundarySlots)
                {
                    frame.HasChild = TryResolve(_buffer, frame.Record.GetLink(frame.NextSlot++), _lowerBound, out frame.Child);
                    continue;
                }

                if (_pending.Count > frame.PendingStart) return EmitLast();
                _stack.RemoveAt(_stack.Count - 1);
            }

            return false;
        }

        private bool EmitLast()
        {
            Current = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
            return true;
        }

        public void Dispose()
        {
            _stack.Dispose();
            _pending.Dispose();
        }

        private struct Frame
        {
            public PbtGroupRecord Record;
            public PbtGroupRecord Child;
            public int NextSlot;
            public int PendingStart;
            public bool HasChild;
        }
    }
}
