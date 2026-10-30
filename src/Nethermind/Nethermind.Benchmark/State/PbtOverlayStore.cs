// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;

namespace Nethermind.Benchmarks.State;

/// <summary>An in-memory store whose writes land in an overlay above a fixed base tree, so the base tree can be reused.</summary>
/// <remarks>
/// Until the first <see cref="ResetOverlay"/> the writes build the base tree. Every group of a tree is reached by pointer from
/// its root, so the store loads nothing by path.
/// </remarks>
internal sealed class PbtOverlayStore : IPbtStore, IDisposable
{
    private readonly PbtPrivateGroupRecordBuffer _base = new(64 << 20);
    private PbtOverlayGroupRecordBuffer? _overlay;

    private IPbtGroupRecordBuffer Buffer => (IPbtGroupRecordBuffer?)_overlay ?? _base;

    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => Buffer.StartWriteBatch(alignedTotalLength);

    public bool TryLocate(ulong pointer, out PbtGroupRecord record) => Buffer.TryLocate(pointer, out record);

    public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => false;

    /// <summary>Discards the previous overlay's writes, keeping the base tree.</summary>
    public void ResetOverlay()
    {
        _overlay?.Dispose();
        _overlay = new PbtOverlayGroupRecordBuffer(_base);
    }

    public void Dispose()
    {
        _overlay?.Dispose();
        _base.Dispose();
    }
}
