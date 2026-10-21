// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>Writes and walks the group trees of an <see cref="IPbtGroupRecordBuffer"/>.</summary>
/// <remarks>
/// A tree is walked from its root record through boundary links. A link names the first physical group below its slot,
/// possibly past compressed absent groups, so a linked record deeper than the wanted path, or off it, proves that the
/// tree has no group there. A record is always reserved after the records it links to, so a tree's records written since
/// a pin are exactly those at or above its write boundary, and a walk of them prunes every link below it.
/// </remarks>
public static class PbtGroupRecordBufferExtensions
{
    /// <summary>Copies an existing canonical group into a new immutable record.</summary>
    public static PbtGroupReference Append(this IPbtGroupRecordBuffer buffer, PbtStorageNodePath path, ReadOnlySpan<byte> payload, in PbtGroupLinks links)
    {
        PbtGroupReference reference = buffer.Reserve(path, payload.Length, links, out Span<byte> destination);
        payload.CopyTo(destination);
        return reference;
    }

    /// <summary>Resolves the group at <paramref name="path"/> in the tree whose root group is <paramref name="root"/>.</summary>
    /// <returns>
    /// The group's direct reference, with its record in <paramref name="record"/>; None when the tree has no group at
    /// <paramref name="path"/>; or Unknown when the walk reaches a link that is unresolved or no longer resident.
    /// </returns>
    /// <remarks>The caller's pin must outlive all uses of the returned record.</remarks>
    public static PbtGroupReference FindGroup(this IPbtGroupRecordBuffer buffer, PbtGroupReference root, PbtStorageNodePath path, out PbtGroupRecord record)
    {
        if (!PbtThreeLevelGroupGeometry.IsGroupDepth(path.BitDepth)) throw new ArgumentException("The path is not a PBT group boundary.", nameof(path));
        record = default;
        Span<byte> target = stackalloc byte[PbtVariableTreeKey.MaxLength];
        Span<byte> linked = stackalloc byte[PbtVariableTreeKey.MaxLength];
        target.Clear();
        PbtNodePathOperations.CopyTo(path, target);
        PbtGroupReference reference = root;
        while (reference.IsDirect)
        {
            if (!buffer.TryLocate(reference, out record)) return PbtGroupReference.Unknown;
            PbtStorageNodePath recordPath = record.Path;
            if (recordPath.BitDepth > path.BitDepth) return PbtGroupReference.None;
            linked.Clear();
            PbtNodePathOperations.CopyTo(recordPath, linked);
            if (PbtKeyOperations.FirstDifferingBit(linked, target, 0) < recordPath.BitDepth) return PbtGroupReference.None;
            if (recordPath.BitDepth == path.BitDepth) return reference;
            reference = record.GetLink(TrieUpdater.BoundarySlot(target, recordPath.BitDepth));
        }
        record = default;
        return reference;
    }
}
