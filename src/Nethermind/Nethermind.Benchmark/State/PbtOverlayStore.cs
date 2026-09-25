// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.Benchmarks.State;

/// <summary>An in-memory store whose writes land in an overlay above fixed base groups, so the base tree can be reused.</summary>
internal sealed class PbtOverlayStore : IPbtStore, IPbtNodeGroupSink, IDisposable
{
    private readonly Dictionary<PbtStorageNodePath, RefCountingMemory> _base = [];
    private readonly Dictionary<PbtStorageNodePath, RefCountingMemory?> _overlay = [];

    public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
    {
        PbtStorageNodePath key = groupKey.ToPath<PbtStorageNodePath>();
        if (!_overlay.TryGetValue(key, out RefCountingMemory? payload) && !_base.TryGetValue(key, out payload)) return null;
        payload?.AcquireLease();
        return payload;
    }

    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
    {
        payload?.AcquireLease();
        PbtStorageNodePath key = groupKey.ToPath<PbtStorageNodePath>();
        if (_overlay.TryGetValue(key, out RefCountingMemory? previous)) ((IDisposable?)previous)?.Dispose();
        _overlay[key] = payload;
    }

    public void CommitOverlay()
    {
        foreach ((PbtStorageNodePath key, RefCountingMemory? payload) in _overlay)
        {
            if (_base.Remove(key, out RefCountingMemory? previous)) ((IDisposable)previous).Dispose();
            if (payload is not null) _base[key] = payload;
        }
        _overlay.Clear();
    }

    public void ResetOverlay()
    {
        foreach (RefCountingMemory? payload in _overlay.Values) ((IDisposable?)payload)?.Dispose();
        _overlay.Clear();
    }

    public void Dispose()
    {
        ResetOverlay();
        foreach (RefCountingMemory payload in _base.Values) ((IDisposable)payload).Dispose();
        _base.Clear();
    }
}
