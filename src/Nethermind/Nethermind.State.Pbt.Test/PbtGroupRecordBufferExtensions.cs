// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.Test;

/// <summary>Operations on the shared group record representation, independent of buffer storage policy.</summary>
/// <remarks>
/// A tree is walked from its root record through boundary links. A link names the first physical group below its slot,
/// possibly past compressed absent groups, so the walk misses at a linked record deeper than the wanted path, or off it.
/// A record is always reserved after the records it links to, so a tree's records written since a pin are exactly those at
/// or above its write boundary, and a walk of them prunes every link below it.
/// </remarks>
public static class PbtGroupRecordBufferExtensions
{
    /// <summary>Appends a canonical group with the given links to the records of its descendants.</summary>
    public static ulong Append(this IPbtGroupRecordBuffer buffer, PbtStorageNodePath path, ReadOnlySpan<byte> payload, in PbtGroupLinks links)
    {
        using IPbtGroupRecordWriteBatch batch = buffer.StartWriteBatch(PbtGroupRecordCodec.AlignedLength(PbtGroupRecordCodec.Length(payload.Length)));
        payload.CopyTo(PbtGroupRecordCodec.WriteGroup(batch.Bytes, path, links, payload.Length));
        return batch.BasePointer;
    }

    /// <summary>Finds the buffer-resident group at the exact <paramref name="path"/> from the given root, or misses at an unavailable link.</summary>
    /// <remarks>The caller's pin must outlive all uses of the returned record.</remarks>
    public static bool TryFindGroup(this IPbtGroupRecordBuffer buffer, ulong rootPointer, PbtStorageNodePath path, out PbtGroupRecord record)
    {
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(path.BitDepth)) throw new ArgumentException("The path is not a PBT group boundary.", nameof(path));
        Span<byte> target = stackalloc byte[PbtVariableTreeKey.MaxLength];
        Span<byte> linked = stackalloc byte[PbtVariableTreeKey.MaxLength];
        target.Clear();
        PbtNodePathOperations.CopyTo(path, target);
        for (ulong pointer = rootPointer; pointer != 0 && buffer.TryLocate(pointer, out record);)
        {
            PbtStorageNodePath recordPath = record.Path;
            if (recordPath.BitDepth > path.BitDepth) break;
            linked.Clear();
            PbtNodePathOperations.CopyTo(recordPath, linked);
            if (PbtKeyOperations.FirstDifferingBit(linked, target, 0) < recordPath.BitDepth) break;
            if (recordPath.BitDepth == path.BitDepth) return true;
            pointer = record.GetLink(TrieUpdater.BoundarySlot(target, recordPath.BitDepth));
        }
        record = default;
        return false;
    }
}
