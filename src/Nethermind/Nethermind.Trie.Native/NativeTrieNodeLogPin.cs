// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Trie.Native;

/// <summary>
/// A pinned generation of a <see cref="NativeTrieNodeLog"/>, which the log keeps, with every later generation, until
/// the pin is disposed.
/// </summary>
public sealed class NativeTrieNodeLogPin : IDisposable
{
    private readonly NativeTrieNodeLog _log;
    private int _disposed;

    internal NativeTrieNodeLogPin(NativeTrieNodeLog log, ulong generation, ulong writeBoundary)
    {
        _log = log;
        Generation = generation;
        WriteBoundary = writeBoundary;
    }

    /// <summary>The pinned generation, later generations being greater.</summary>
    public ulong Generation { get; }

    /// <summary>The first unreserved record position when this pin was acquired.</summary>
    public ulong WriteBoundary { get; }

    public NativeTrieNodeLog Log => _log;

    /// <summary>Pins <see cref="Generation"/> again, for as long as the returned pin.</summary>
    public NativeTrieNodeLogPin Copy() => _log.Pin(Generation, WriteBoundary);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _log.Unpin(Generation);
    }
}
