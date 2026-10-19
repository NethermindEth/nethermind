// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt.NodeLog;

/// <remarks>
/// A tree is walked from its root record through boundary links. A link names the first physical group below its slot,
/// possibly past compressed absent groups, so a linked record deeper than the wanted path, or off it, proves that the
/// tree has no group there. A record is always reserved after the records it links to, so a tree's records written since
/// a pin are exactly those at or above its write boundary, and the walk prunes every link below it.
/// </remarks>
public sealed partial class PbtGroupLog
{
    /// <summary>Resolves the group at <paramref name="path"/> in the tree whose root group is <paramref name="root"/>.</summary>
    /// <returns>
    /// The group's direct reference, with its record in <paramref name="record"/>; None when the tree has no group at
    /// <paramref name="path"/>; or Unknown when the walk reaches a link that is unresolved or no longer resident.
    /// </returns>
    /// <remarks>The caller's pin must outlive all uses of the returned record.</remarks>
    public PbtGroupReference FindGroup(PbtGroupReference root, PbtStorageNodePath path, out PbtGroupRecord record)
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
            if (!TryGetRecord(reference, out record)) return PbtGroupReference.Unknown;
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

    /// <summary>Enumerates the records of the tree under <paramref name="root"/> reserved at or after <paramref name="lowerBound"/>.</summary>
    /// <remarks>
    /// Links below <paramref name="lowerBound"/>, and unresolved or absent links, are not followed. A pin at or below
    /// <paramref name="lowerBound"/> must outlive the enumeration and every use of the records it yields.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A record at or above <paramref name="lowerBound"/> is no longer resident.</exception>
    public IEnumerable<PbtGroupRecord> EnumerateGroups(PbtGroupReference root, PbtGroupReference lowerBound)
    {
        Stack<PbtGroupReference> pending = new();
        pending.Push(root);
        while (pending.TryPop(out PbtGroupReference reference))
        {
            if (!reference.IsDirect || reference.CompareTo(lowerBound) < 0) continue;
            if (!TryGetRecord(reference, out PbtGroupRecord record))
                throw new InvalidOperationException("A group record written since the lower bound is no longer resident.");
            yield return record;
            PbtGroupLinks links = record.Links;
            for (int slot = PbtThreeLevelGroupGeometry.BoundarySlots - 1; slot >= 0; slot--) pending.Push(links[slot]);
        }
    }
}
