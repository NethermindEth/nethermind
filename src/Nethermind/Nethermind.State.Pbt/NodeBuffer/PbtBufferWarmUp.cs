// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>Loads the node groups on a key's path into a <see cref="PbtWarmedGroupBuffer"/> ahead of a <see cref="TrieUpdater"/> fold that changes it.</summary>
public static class PbtBufferWarmUp
{
    // Fits a path of about forty large groups, past any tree a hash-keyed path builds.
    private const int RecordsLength = 64 * 1024;

    private static readonly ReadOnlySpanAction<byte, ArrayPoolList<byte>> CopyPayload = static (payload, scratch) => scratch.AddRange(payload);

    /// <summary>Reads a pre-block path, then publishes its newly fetched records in one write batch.</summary>
    /// <remarks>
    /// Descends while reading and publishes only once the path is read, so every published record links the record below it,
    /// and indexes their paths only once all are written. Stops silently where the path ends or <paramref name="reader"/>
    /// misses, retaining its verified prefix.
    /// </remarks>
    /// <param name="buffer">The buffer holding the pre-block tree, which also resolves the pointers of <paramref name="warmed"/>.</param>
    /// <param name="reader">Reads the groups neither <paramref name="buffer"/> nor <paramref name="warmed"/> holds.</param>
    /// <param name="root">The tree's root before the block and its group's pointer, or 0 when the buffer does not hold it.</param>
    public static void WarmUp<TKey, TPath>(IPbtGroupRecordBuffer buffer, PbtWarmedGroupBuffer warmed, PbtReadOnlySnapshotBundle reader,
        (ValueHash256 Hash, ulong Pointer) root, TKey key)
        where TKey : unmanaged, IPbtKey<TKey>
        where TPath : struct, IPbtNodePath<TPath>
    {
        if (root.Hash == default) return;
        using ArrayPoolList<Frame> frames = new(PbtStorageNodePath.MaxBitDepth / PbtFourLevelGroupGeometry.LevelsPerGroup + 1);
        using ArrayPoolList<byte> scratch = new(1024);
        using ArrayPoolList<byte> records = new(SafeArrayPool<byte>.Shared, RecordsLength, RecordsLength, clearFirst: false);
        int recordsLength = 0;

        PbtStorageNodePath path = default;
        ulong pointer = root.Pointer;
        TrieUpdater<TKey, TPath>.BoundaryNode input = default;
        while (TryRead(buffer, warmed, reader, path, pointer, scratch, records, ref recordsLength, out Frame frame))
        {
            if (path.BitDepth == 0) input = new GroupFrameReader<TKey, TPath>(frame.Record).TakeRoot(root.Hash);
            bool descends = TrieUpdater<TKey, TPath>.TryFindChildGroup(frame.Record, input, key, out int slot, out TrieUpdater<TKey, TPath>.BoundaryNode child, out int childDepth);
            if (descends) frame.ChildSlot = slot;
            frames.Add(frame);
            if (!descends) break;
            input = child;
            path = GroupOf(key.Bytes, childDepth);
            pointer = child.GroupPointer;
        }

        if (recordsLength == 0) return;

        using (IPbtGroupRecordWriteBatch batch = warmed.StartWriteBatch(recordsLength))
        {
            Span<byte> written = batch.Bytes;
            records.AsSpan()[..recordsLength].CopyTo(written);
            ulong childPointer = 0;
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                ref Frame frame = ref frames.GetRef(i);
                if (frame.RecordOffset >= 0)
                {
                    frame.Pointer = batch.BasePointer + (ulong)frame.RecordOffset;
                    if (frame.ChildSlot >= 0 && childPointer != 0)
                        MemoryMarshal.AsRef<PbtGroupRecordHeader>(written[frame.RecordOffset..]).Links[frame.ChildSlot] = childPointer;
                }

                childPointer = frame.Pointer;
            }
        }

        foreach (Frame frame in frames)
        {
            if (frame.RecordOffset >= 0) warmed.Index(frame.Path, frame.Pointer);
        }
    }

    /// <summary>Reads the group <paramref name="pointer"/> or <paramref name="path"/> names in place, or writes its record from <paramref name="reader"/> to <paramref name="records"/>.</summary>
    /// <remarks>A read payload is trusted to be the group its parent's boundary leads to: the pre-block path reaches the reader only below a subtree unchanged since persistence.</remarks>
    /// <param name="recordsLength">The bytes of <paramref name="records"/> in use, advanced past a written record and its alignment padding.</param>
    /// <returns>False when the reader misses, or the record does not fit <paramref name="records"/>.</returns>
    private static bool TryRead(IPbtGroupRecordBuffer buffer, PbtWarmedGroupBuffer warmed, PbtReadOnlySnapshotBundle reader,
        in PbtStorageNodePath path, ulong pointer, ArrayPoolList<byte> scratch, ArrayPoolList<byte> records, ref int recordsLength, out Frame frame)
    {
        frame = new Frame { Path = path, RecordOffset = -1, ChildSlot = -1 };
        if (buffer.TryLocate(pointer, out PbtGroupRecord record) || warmed.TryGet(path, out record, out pointer))
        {
            frame.Record = record;
            frame.Pointer = pointer;
            return true;
        }

        scratch.Clear();
        if (!reader.TryLoadPersistedNodeGroup(path, CopyPayload, scratch)) return false;

        int length = PbtGroupRecordCodec.AlignedLength(PbtGroupRecordCodec.Length(scratch.Count));
        if (length > records.Count - recordsLength) return false;
        scratch.AsSpan().CopyTo(PbtGroupRecordCodec.WriteGroup(records.AsSpan()[recordsLength..], path, default, scratch.Count));
        frame.Record = new PbtGroupRecord(records.AsReadOnlyMemory(), recordsLength);
        frame.RecordOffset = recordsLength;
        recordsLength += length;
        return true;
    }

    /// <summary>The path of the group at <paramref name="depth"/> above the leaf at <paramref name="key"/>.</summary>
    private static PbtStorageNodePath GroupOf(ReadOnlySpan<byte> key, int depth)
    {
        Span<byte> path = stackalloc byte[(depth + 7) / 8];
        key[..path.Length].CopyTo(path);
        if (depth % 8 != 0) path[^1] &= (byte)(0xFF << (8 - depth % 8));
        return new PbtStorageNodePath(path, depth);
    }

    /// <summary>A group on the warmed path: a record read in place, or one written to the scratch records, to be published.</summary>
    private struct Frame
    {
        public PbtStorageNodePath Path;
        public PbtGroupRecord Record;
        // The pointer of the record, 0 for a scratch record until it is published.
        public ulong Pointer;
        // The offset of a scratch record in the scratch records, or -1 for a record read in place.
        public int RecordOffset;
        // The boundary slot of the group below on the path, or -1 where the path ends.
        public int ChildSlot;
    }
}
