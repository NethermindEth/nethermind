// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>Writes and walks the group trees of an <see cref="IPbtGroupRecordBuffer"/>.</summary>
/// <remarks>
/// A tree is walked from its root record through boundary links. A link names the first physical group below its slot,
/// possibly past compressed absent groups, so the walk misses at a linked record deeper than the wanted path, or off it.
/// A record is always reserved after the records it links to, so a tree's records written since a pin are exactly those at
/// or above its write boundary, and a walk of them prunes every link below it.
/// </remarks>
public static class PbtGroupRecordBufferExtensions
{
    /// <summary>Copies an existing canonical group into a new immutable record.</summary>
    public static ulong Append(this IPbtGroupRecordBuffer buffer, PbtStorageNodePath path, ReadOnlySpan<byte> payload, in PbtGroupLinks links)
    {
        ulong pointer = buffer.Reserve(path, payload.Length, links, out Span<byte> destination);
        payload.CopyTo(destination);
        return pointer;
    }

    /// <summary>Finds the buffer-resident group at the exact <paramref name="path"/> from the given root, or misses at an unavailable link.</summary>
    /// <remarks>The caller's pin must outlive all uses of the returned record.</remarks>
    public static bool TryFindGroup(this IPbtGroupRecordBuffer buffer, ulong rootPointer, PbtStorageNodePath path, out PbtGroupRecord record)
    {
        if (!PbtThreeLevelGroupGeometry.IsGroupDepth(path.BitDepth)) throw new ArgumentException("The path is not a PBT group boundary.", nameof(path));
        Span<byte> target = stackalloc byte[PbtVariableTreeKey.MaxLength];
        Span<byte> linked = stackalloc byte[PbtVariableTreeKey.MaxLength];
        target.Clear();
        PbtNodePathOperations.CopyTo(path, target);
        for (ulong pointer = rootPointer; buffer.TryLocate(pointer, out record);)
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
