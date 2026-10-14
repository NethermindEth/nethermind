// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Trie.NodeLog;

/// <summary>The path and log pointer of every node bulk sets wrote, in write order.</summary>
internal sealed class WrittenNodes : IDisposable
{
    private const int InitialCapacity = 16;

    private readonly Lock _lock = new();
    private readonly ArrayPoolList<(TreePath Path, ulong Pointer)> _nodes = new(InitialCapacity);

    public int Count => _nodes.Count;

    public void Add(in TreePath path, ulong pointer)
    {
        lock (_lock) _nodes.Add((path, pointer));
    }

    public void AddRange(ReadOnlySpan<(TreePath Path, ulong Pointer)> nodes)
    {
        lock (_lock) _nodes.AddRange(nodes);
    }

    /// <remarks>No add may run concurrently.</remarks>
    public ReadOnlySpan<(TreePath Path, ulong Pointer)> AsSpan() => _nodes.AsSpan();

    public void Clear() => _nodes.Clear();

    public void Dispose() => _nodes.Dispose();
}
