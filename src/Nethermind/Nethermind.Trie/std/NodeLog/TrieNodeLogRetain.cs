// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Trie.NodeLog;

/// <summary>Keeps a generation of a <see cref="TrieNodeLog"/>, and every later one, readable in place until disposed.</summary>
/// <remarks>Obtained from <see cref="TrieNodeLog.RetainActive"/> or <see cref="Copy"/>, which have already retained <see cref="Generation"/>.</remarks>
public sealed class TrieNodeLogRetain(TrieNodeLog log, ulong generation) : IDisposable
{
    private int _disposed;

    /// <summary>The log whose generation is retained.</summary>
    public TrieNodeLog Log { get; } = log;

    /// <summary>The id of the retained generation.</summary>
    public ulong Generation { get; } = generation;

    /// <summary>A further, independently disposed retain of the same generation.</summary>
    public TrieNodeLogRetain Copy()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        TrieNodeLogRetain copy = new(Log, Generation);
        Log.Retain(Generation);
        return copy;
    }

    /// <summary>Releases the generation; later calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Log.Release(Generation);
    }
}
