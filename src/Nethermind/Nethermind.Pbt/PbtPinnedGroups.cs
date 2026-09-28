// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

namespace Nethermind.Pbt;

/// <summary>Holds the node groups at the top of one immutable tree, so repeated traversals read them by path without leasing, fetching or hashing.</summary>
/// <remarks>
/// Every traversal passes through these groups, so pinning them removes the shared lease counts that concurrent
/// traversals would otherwise contend on. Only groups at depths up to <see cref="MaxDepth"/> are held; a path identifies
/// a group's content only within one state, so an instance must not outlive or span states.
/// </remarks>
internal sealed class PbtPinnedGroups : IDisposable
{
    /// <summary>The deepest group depth within the zone byte.</summary>
    internal static readonly int MaxDepth = 8 / PbtGroupGeometry.LevelsPerGroup * PbtGroupGeometry.LevelsPerGroup;

    // One slot per group at each group depth up to MaxDepth, the deeper depths after the shallower ones.
    private readonly RefCountingMemory?[] _groups = new RefCountingMemory?[LevelOffset(MaxDepth + PbtGroupGeometry.LevelsPerGroup)];

    /// <summary>Returns the pinned payload of <paramref name="groupKey"/>, borrowed for as long as this instance is alive, or null.</summary>
    internal RefCountingMemory? Get(PbtStorageNodePath groupKey) =>
        groupKey.BitDepth > MaxDepth ? null : Volatile.Read(ref _groups[IndexOf(groupKey)]);

    /// <summary>Pins <paramref name="payload"/> under its own lease when <paramref name="groupKey"/> is a top group not pinned yet; the caller keeps its lease.</summary>
    internal void TryPin(PbtStorageNodePath groupKey, RefCountingMemory payload)
    {
        if (groupKey.BitDepth > MaxDepth || Volatile.Read(ref _groups[IndexOf(groupKey)]) is not null) return;
        payload.AcquireLease();
        if (Interlocked.CompareExchange(ref _groups[IndexOf(groupKey)], payload, null) is not null) ((IDisposable)payload).Dispose();
    }

    private static int IndexOf(PbtStorageNodePath groupKey) =>
        groupKey.BitDepth == 0 ? 0 : LevelOffset(groupKey.BitDepth) + (groupKey.GetByte(0) >> (8 - groupKey.BitDepth));

    /// <summary>The number of groups at the group depths shallower than <paramref name="depth"/>, a geometric series over the slots per group.</summary>
    private static int LevelOffset(int depth) => ((1 << depth) - 1) / (PbtGroupGeometry.BoundarySlots - 1);

    /// <remarks>Must not run concurrently with a traversal borrowing the pinned payloads.</remarks>
    public void Dispose()
    {
        foreach (ref RefCountingMemory? group in _groups.AsSpan())
        {
            ((IDisposable?)group)?.Dispose();
            group = null;
        }
    }
}
