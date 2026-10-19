// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Trie;

namespace Nethermind.State.Flat.NodeBuffer;

public sealed partial class NodeRecordBuffer
{
    private int _pinCount;
    private bool _disposed;
    private Exception? _compactionFailure;

    /// <remarks>Only storage of a generation no pin covers is retired, so no reader can still borrow it.</remarks>
    private static void Retire(IDisposable? owner) => owner?.Dispose();

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Volatile.Read(ref _compactionFailure) is { } failure)
            throw new TrieException("Trie node buffer compaction failed.", failure);
    }

    /// <summary>Stops new operations and releases native storage once outstanding pins and compaction finish.</summary>
    /// <remarks>Does not wait for outstanding pins, which may be released by the disposing thread itself. Later calls do nothing.</remarks>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            TryCompleteDispose();
        }
    }

    private void TryCompleteDispose()
    {
        if (!_disposed || _pinCount != 0 || _compacting) return;

        foreach (Generation? generation in _generations)
        {
            if (generation is null) continue;
            Retire(generation.Data);
            Retire(generation.Bloom);
            Retire(generation.Compressed);
            generation.Data = null;
            generation.Bloom = null;
            generation.Compressed = null;
        }
        _inPlaceBytes = 0;
        _compressedMemory = 0;
        UpdateMetrics();
    }
}
