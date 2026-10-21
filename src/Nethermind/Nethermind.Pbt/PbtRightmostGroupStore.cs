// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

/// <summary>
/// Node-group store for folding strictly ascending leaves in windows from an empty tree; it retains only the rightmost
/// group at each depth and hands every group record to <paramref name="writeThrough"/>.
/// </summary>
/// <remarks>
/// A key above every folded key reaches an existing group only when that group's path prefixes the largest folded key,
/// so the rightmost group at each depth is the only one a later window can read, and every other group is only written through.
/// Zones and buckets fold concurrently, so a group superseded during a fold stays readable until the fold ends.
/// </remarks>
/// <param name="writeThrough">Receives every group record the fold writes, borrowed for the call, one call at a time under this store's lock.</param>
public sealed class PbtRightmostGroupStore(Action<PbtGroupRecord> writeThrough) : IPbtStore
{
    private readonly Lock _lock = new();
    private readonly Group[] _edge = new Group[PbtVariableTreeKey.MaxLength * 8 + 1];
    /// <summary>The group each depth held when the fold started, once superseded; released when the fold ends.</summary>
    private readonly Group[] _superseded = new Group[PbtVariableTreeKey.MaxLength * 8 + 1];

    public bool TryLocate(ulong pointer, out PbtGroupRecord record)
    {
        record = default;
        return false;
    }

    public bool TryLoad(scoped in PbtTraversalPath path, out PbtGroupRecord record)
    {
        PbtStorageNodePath groupPath = path.ToPath<PbtStorageNodePath>();
        lock (_lock)
        {
            Group group = _edge[path.BitDepth];
            if (!group.Holds(groupPath)) group = _superseded[path.BitDepth];
            record = group.Holds(groupPath) ? new PbtGroupRecord(group.Record, 0) : default;
            return group.Holds(groupPath);
        }
    }

    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new PbtRecordCallbackWriteBatch(Retain, alignedTotalLength);

    private void Retain(PbtGroupRecord record)
    {
        PbtStorageNodePath path = record.Path;
        lock (_lock)
        {
            writeThrough(record);
            ref Group edge = ref _edge[path.BitDepth];
            if (edge.Record is not null && path.CompareTo(edge.Path) < 0) return;
            ref Group superseded = ref _superseded[path.BitDepth];
            if (superseded.Record is null) superseded = edge;
            edge = new(path, record.Span.ToArray());
        }
    }

    /// <summary>Forgets the groups the last fold superseded; call once each fold ends.</summary>
    public void ReleaseSuperseded() => Array.Clear(_superseded);

    /// <param name="Record">The group's record bytes, kept in a managed array so a borrowed record stays valid after it is superseded.</param>
    private readonly record struct Group(PbtStorageNodePath Path, byte[]? Record)
    {
        public bool Holds(in PbtStorageNodePath path) => Record is not null && Path.Equals(path);
    }
}
