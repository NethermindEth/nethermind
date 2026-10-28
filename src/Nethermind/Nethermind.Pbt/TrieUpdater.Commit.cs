// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Metric;
using Nethermind.Pbt.NodeBuffer;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

public static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    public sealed partial class FoldWorkingState
    {
        /// <summary>How many pending groups a pooled state keeps room for; a state grown past it is not pooled.</summary>
        private const int MaxPooledPendingGroups = 4096;

        private readonly ArrayPoolList<PendingGroup> _pendingGroups = new(16);

        private bool IsPoolable => _pendingGroups.Capacity <= MaxPooledPendingGroups;

        /// <summary>Takes <paramref name="writer"/>'s finished group of <paramref name="payloadLength"/> bytes, detached into the store by the next <see cref="Commit"/>.</summary>
        /// <remarks>The writer is kept until then, so the group is written once, straight into the commit's batch, and goes back to the pool after.</remarks>
        /// <returns>The pointer standing for the group in its parent's links until it is committed.</returns>
        public ulong AddPendingGroup(PbtNodeGroupWriter<TPath> writer, in PbtStorageNodePath path, in PbtGroupLinks links,
            ReadOnlySpan<long> descendantBytes, ushort candidateSlots, int payloadLength)
        {
            PendingGroup group = new()
            {
                Writer = writer,
                Path = path,
                Links = links,
                CandidateSlots = candidateSlots,
                PayloadLength = payloadLength,
                Length = PbtGroupRecordCodec.AlignedLength(PbtGroupRecordCodec.Length(payloadLength)),
            };
            descendantBytes.CopyTo(group.DescendantBytes);
            _pendingGroups.Add(group);
            return (ulong)_pendingGroups.Count;
        }

        /// <summary>Writes the pending group at <paramref name="top"/> and every pending group it links below it to <paramref name="store"/>, then drops every pending group.</summary>
        /// <remarks>
        /// One walk sums the records' lengths, so a single batch is reserved, and a second writes them into it in post-order,
        /// each group after the groups it links, so its links take their pointers. Pending groups <paramref name="top"/> does not
        /// reach are dropped unwritten. The walks are timed on <paramref name="phaseTime"/>, when set, as the
        /// <see cref="FoldPhaseLabels.CommitSize"/> and <see cref="FoldPhaseLabels.CommitWrite"/> phases.
        /// </remarks>
        /// <returns>The pointer of the committed top group, or <paramref name="top"/> itself when it is not pending.</returns>
        public ulong Commit(IPbtStore store, ulong top, IMetricObserver? phaseTime, FoldPhaseLabels? phaseLabels)
        {
            try
            {
                if (!IsPending(top)) return top;

                int topIndex = (int)top - 1;
                long start = phaseTime is null ? 0 : Stopwatch.GetTimestamp();
                int totalLength = PendingLength(topIndex);
                long sized = phaseTime is null ? 0 : Stopwatch.GetTimestamp();
                ulong pointer;
                using (IPbtGroupRecordWriteBatch batch = store.StartWriteBatch(totalLength))
                {
                    int offset = 0;
                    pointer = WritePending(batch.Bytes, batch.BasePointer, topIndex, ref offset);
                }

                if (phaseTime is not null)
                {
                    phaseTime.Observe(sized - start, phaseLabels!.CommitSize);
                    phaseTime.Observe(Stopwatch.GetTimestamp() - sized, phaseLabels.CommitWrite);
                }
                return pointer;
            }
            finally
            {
                ClearPending();
            }
        }

        /// <summary>Drops every pending group, returning the writers of those never committed.</summary>
        private void ClearPending()
        {
            foreach (PendingGroup group in _pendingGroups.AsSpan())
            {
                if (group.Writer is not null) ReturnWriter(group.Writer);
            }
            _pendingGroups.Clear();
        }

        /// <summary>The aligned length of the pending group at <paramref name="index"/> and of its pending descendants.</summary>
        private int PendingLength(int index)
        {
            ref PendingGroup group = ref _pendingGroups.GetRef(index);
            int length = group.Length;
            for (int slot = 0; slot < PbtTwoLevelGroupGeometry.BoundarySlots; slot++)
            {
                if (IsPending(group.Links[slot])) length += PendingLength((int)group.Links[slot] - 1);
            }
            return length;
        }

        /// <summary>Writes the pending descendants of the group at <paramref name="index"/>, then the group, from <paramref name="offset"/> on.</summary>
        /// <returns>The group's pointer.</returns>
        private ulong WritePending(Span<byte> bytes, ulong basePointer, int index, ref int offset)
        {
            ref PendingGroup group = ref _pendingGroups.GetRef(index);
            for (int slot = 0; slot < PbtTwoLevelGroupGeometry.BoundarySlots; slot++)
            {
                if (IsPending(group.Links[slot])) group.Links[slot] = WritePending(bytes, basePointer, (int)group.Links[slot] - 1, ref offset);
            }

            int start = offset;
            group.Writer!.DetachTo(PbtGroupRecordCodec.WriteGroup(bytes[start..], group.Path, group.Links, group.PayloadLength), group.DescendantBytes,
                group.CandidateSlots);
            ReturnWriter(group.Writer);
            group.Writer = null;
            offset += group.Length;
            // A store keeping groups by path links no pointer, so its batch has none.
            return basePointer == 0 ? 0 : basePointer + (ulong)start;
        }

        private void DisposePending()
        {
            foreach (PendingGroup group in _pendingGroups.AsSpan()) group.Writer?.Dispose();
            _pendingGroups.Dispose();
        }

        /// <summary>Whether <paramref name="pointer"/> stands for a pending group: one of generation 0, which no store's pointer has.</summary>
        private static bool IsPending(ulong pointer) => pointer != 0 && pointer >> PbtGroupRecordCodec.OffsetBits == 0;

        private struct PendingGroup
        {
            /// <summary>The writer holding the group until it is committed, then null.</summary>
            public PbtNodeGroupWriter<TPath>? Writer;
            public PbtStorageNodePath Path;
            public PbtGroupLinks Links;
            public DescendantBytesBuffer DescendantBytes;
            public ushort CandidateSlots;
            public int PayloadLength;
            public int Length;
        }

        [InlineArray(PbtTwoLevelGroupGeometry.BoundarySlots)]
        private struct DescendantBytesBuffer
        {
            private long _element;
        }
    }
}
