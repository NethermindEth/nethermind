// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Memory;
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
public sealed class PbtRightmostGroupStore(Action<PbtGroupRecord> writeThrough) : IPbtStore, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Group[] _edge = new Group[PbtVariableTreeKey.MaxLength * 8 + 1];
    /// <summary>The group each depth held when the fold started, once superseded; released when the fold ends.</summary>
    private readonly Group[] _superseded = new Group[PbtVariableTreeKey.MaxLength * 8 + 1];

    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey)
    {
        PbtStorageNodePath path = groupKey.ToPath<PbtStorageNodePath>();
        lock (_lock)
        {
            Group group = _edge[groupKey.BitDepth];
            if (!group.Holds(path)) group = _superseded[groupKey.BitDepth];
            if (!group.Holds(path)) return null;
            group.Payload!.AcquireLease();
            return group.Payload;
        }
    }

    public IPbtConcurrentWriter CreateWriter() => new PbtRecordCallbackWriter(Retain);

    private void Retain(PbtGroupRecord record)
    {
        PbtStorageNodePath path = record.Path;
        lock (_lock)
        {
            writeThrough(record);
            ref Group edge = ref _edge[path.BitDepth];
            if (edge.Payload is not null && path.CompareTo(edge.Path) < 0) return;
            RefCountingMemory payload = PooledRefCountingMemoryProvider.Instance.Rent(record.Payload.Length);
            record.Payload.Span.CopyTo(payload.GetSpan());
            ref Group superseded = ref _superseded[path.BitDepth];
            if (superseded.Payload is null) superseded = edge;
            else ((IDisposable?)edge.Payload)?.Dispose();
            edge = new(path, payload);
        }
    }

    /// <summary>Releases the groups the last fold superseded; call once each fold ends.</summary>
    public void ReleaseSuperseded()
    {
        foreach (ref Group group in _superseded.AsSpan())
        {
            ((IDisposable?)group.Payload)?.Dispose();
            group = default;
        }
    }

    public void Dispose()
    {
        ReleaseSuperseded();
        foreach (Group group in _edge) ((IDisposable?)group.Payload)?.Dispose();
    }

    private readonly record struct Group(PbtStorageNodePath Path, RefCountingMemory? Payload)
    {
        public bool Holds(in PbtStorageNodePath path) => Payload is not null && Path.Equals(path);
    }
}
