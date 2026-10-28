// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

public static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    public sealed partial class FoldWorkingState
    {
        /// <summary>How many pending groups a pooled state keeps room for; a state grown past it is not pooled.</summary>
        private const int MaxPooledPendingGroups = 4096;
        private const int MaxLevels = PbtVariableTreeKey.MaxLength * 8 / PbtThreeLevelGroupGeometry.LevelsPerGroup + 1;

        private readonly ArrayPoolList<PendingGroup> _pendingGroups = new(16);
        /// <summary>The pending records, laid out one after another, from <see cref="ArrayPool{T}.Shared"/>.</summary>
        private byte[] _pendingRecords = [];
        private int _pendingBytes;
        private readonly ArrayPoolList<int> _collected = new(16);
        private readonly ArrayPoolList<int> _order = new(16);

        private bool IsPoolable => _pendingGroups.Capacity <= MaxPooledPendingGroups;

        /// <summary>Takes a zero-filled record of <paramref name="alignedLength"/> bytes for a group at <paramref name="bitDepth"/>, written to the store by the next <see cref="Commit"/>.</summary>
        /// <param name="pointer">The pointer standing for the record in its parent's links until it is committed.</param>
        public Span<byte> AddPendingGroup(int alignedLength, int bitDepth, out ulong pointer)
        {
            if (_pendingBytes + alignedLength > _pendingRecords.Length)
            {
                byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(_pendingBytes + alignedLength, 2 * _pendingRecords.Length));
                _pendingRecords.AsSpan(0, _pendingBytes).CopyTo(grown);
                if (_pendingRecords.Length != 0) ArrayPool<byte>.Shared.Return(_pendingRecords);
                _pendingRecords = grown;
            }

            _pendingGroups.Add(new PendingGroup { Offset = _pendingBytes, Length = alignedLength, BitDepth = bitDepth });
            pointer = (ulong)_pendingGroups.Count;
            Span<byte> record = _pendingRecords.AsSpan(_pendingBytes, alignedLength);
            record.Clear();
            _pendingBytes += alignedLength;
            return record;
        }

        /// <summary>Writes the pending group at <paramref name="top"/> and every pending group it links below it to <paramref name="store"/>, then drops every pending group.</summary>
        /// <remarks>
        /// Groups are committed one depth at a time, deepest first: two groups of one depth are never
        /// each other's ancestor, and by then every group they link is committed, so a level is reserved in the store at once, with
        /// its children's pointers. Pending groups <paramref name="top"/> does not reach are dropped unwritten.
        /// </remarks>
        /// <returns>The pointer of the committed top group, or <paramref name="top"/> itself when it is not pending.</returns>
        public ulong Commit(IPbtStore store, ulong top)
        {
            try
            {
                if (!IsPending(top)) return top;

                int topIndex = (int)top - 1;
                int count = CollectPending(topIndex);
                Span<int> levelEnds = stackalloc int[MaxLevels + 1];
                Span<int> positions = stackalloc int[MaxLevels + 1];
                levelEnds.Clear();
                Span<int> collected = _collected.AsSpan();
                for (int i = 0; i < count; i++) levelEnds[Level(collected[i])]++;

                // Suffix sums, so level l takes [levelEnds[l + 1], levelEnds[l]) of the order.
                for (int level = MaxLevels - 1; level >= 0; level--) levelEnds[level] += levelEnds[level + 1];

                levelEnds.CopyTo(positions);
                _order.Clear();
                _order.AddRange(collected);
                Span<int> order = _order.AsSpan();
                foreach (int index in collected) order[--positions[Level(index)]] = index;

                for (int level = MaxLevels - 1; level >= 0; level--)
                {
                    if (levelEnds[level] != levelEnds[level + 1]) CommitLevel(store, order[levelEnds[level + 1]..levelEnds[level]]);
                }

                return _pendingGroups[topIndex].Pointer;
            }
            finally
            {
                ClearPending();
            }
        }

        private void ClearPending()
        {
            _pendingGroups.Clear();
            _pendingBytes = 0;
        }

        /// <summary>Lists the pending group at <paramref name="top"/> and its pending descendants, setting their parent and slot.</summary>
        /// <returns>The number listed.</returns>
        private int CollectPending(int top)
        {
            _collected.Clear();
            _collected.Add(top);
            _pendingGroups.GetRef(top).Parent = -1;
            for (int head = 0; head < _collected.Count; head++)
            {
                int parent = _collected[head];
                ref PbtGroupLinks links = ref Header(parent).Links;
                for (int slot = 0; slot < PbtThreeLevelGroupGeometry.BoundarySlots; slot++)
                {
                    if (!IsPending(links[slot])) continue;
                    int child = (int)links[slot] - 1;
                    ref PendingGroup group = ref _pendingGroups.GetRef(child);
                    group.Parent = parent;
                    group.Slot = (byte)slot;
                    _collected.Add(child);
                }
            }

            return _collected.Count;
        }

        private void CommitLevel(IPbtStore store, ReadOnlySpan<int> level)
        {
            int totalLength = 0;
            foreach (int index in level) totalLength += _pendingGroups[index].Length;

            using IPbtGroupRecordWriteBatch batch = store.StartWriteBatch(totalLength);
            Span<byte> bytes = batch.Bytes;
            ulong basePointer = batch.BasePointer;
            int start = 0;
            foreach (int index in level)
            {
                ref PendingGroup group = ref _pendingGroups.GetRef(index);
                _pendingRecords.AsSpan(group.Offset, group.Length).CopyTo(bytes[start..]);
                // A store keeping groups by path links no pointer, so its batch has none.
                group.Pointer = basePointer == 0 ? 0 : basePointer + (ulong)start;
                if (group.Parent >= 0) Header(group.Parent).Links[group.Slot] = group.Pointer;
                start += group.Length;
            }
        }

        private int Level(int index) => _pendingGroups[index].BitDepth / PbtThreeLevelGroupGeometry.LevelsPerGroup;

        private ref PbtGroupRecordHeader Header(int index) =>
            ref MemoryMarshal.AsRef<PbtGroupRecordHeader>(_pendingRecords.AsSpan(_pendingGroups[index].Offset));

        private void DisposePending()
        {
            _pendingGroups.Dispose();
            _collected.Dispose();
            _order.Dispose();
            if (_pendingRecords.Length != 0) ArrayPool<byte>.Shared.Return(_pendingRecords);
            _pendingRecords = [];
        }

        /// <summary>Whether <paramref name="pointer"/> stands for a pending group: one of generation 0, which no store's pointer has.</summary>
        private static bool IsPending(ulong pointer) => pointer != 0 && pointer >> PbtGroupRecordCodec.OffsetBits == 0;

        private struct PendingGroup
        {
            public int Offset;
            public int Length;
            public int BitDepth;
            public int Parent;
            public byte Slot;
            public ulong Pointer;
        }
    }
}
