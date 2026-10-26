// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Benchmarks.State;

/// <summary>An in-memory store whose writes land in an overlay above fixed base groups, so the base tree can be reused.</summary>
/// <remarks>
/// Parallel folds read and publish from several threads, each group from one, so the overlay is a concurrent map; the base is read-only once built.
/// Groups are kept as managed record bytes, so a borrowed record stays valid after its group is replaced.
/// </remarks>
internal sealed class PbtOverlayStore : IPbtStore
{
    private readonly Dictionary<PbtStorageNodePath, byte[]> _base = [];
    private readonly ConcurrentDictionary<PbtStorageNodePath, byte[]> _overlay = new();

    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new PbtRecordCallbackWriteBatch(SetNodeGroup, alignedTotalLength);

    public bool TryLocate(ulong pointer, out PbtGroupRecord record)
    {
        record = default;
        return false;
    }

    public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
    {
        PbtStorageNodePath key = path.ToPath<PbtStorageNodePath>();
        if (!_overlay.TryGetValue(key, out byte[]? bytes) && !_base.TryGetValue(key, out bytes)) return false;
        action(bytes, arg);
        return true;
    }

    private void SetNodeGroup(PbtGroupRecord record) => _overlay[record.Path] = record.Span.ToArray();

    public void CommitOverlay()
    {
        foreach ((PbtStorageNodePath key, byte[] bytes) in _overlay) _base[key] = bytes;
        _overlay.Clear();
    }

    public void ResetOverlay() => _overlay.Clear();
}
