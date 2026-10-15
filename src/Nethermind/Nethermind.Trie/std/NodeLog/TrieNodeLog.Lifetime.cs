// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Trie.NodeLog;

public sealed partial class TrieNodeLog
{
    private readonly List<IDisposable> _retired = [];
    private int _readers;
    private int _retainCount;
    private bool _disposed;
    private Exception? _compactionFailure;

    /// <summary>Protects every borrowed record until the whole operation, including its parallel workers, has finished.</summary>
    internal ReadLease BeginRead()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            _readers++;
            return new ReadLease(this);
        }
    }

    private void EndRead()
    {
        lock (_lock)
        {
            Debug.Assert(_readers > 0);
            if (--_readers == 0) DrainRetired();
            TryCompleteDispose();
        }
    }

    private void Retire(IDisposable? owner)
    {
        if (owner is null) return;
        if (_readers == 0) owner.Dispose();
        else _retired.Add(owner);
    }

    private void DrainRetired()
    {
        foreach (IDisposable owner in _retired) owner.Dispose();
        _retired.Clear();
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Volatile.Read(ref _compactionFailure) is { } failure)
            throw new TrieException("Trie node log compaction failed.", failure);
    }

    /// <summary>Stops new operations and releases native storage once outstanding readers, retains and compaction finish.</summary>
    /// <remarks>Does not wait for outstanding owners, which may be released by the disposing thread itself. Later calls do nothing.</remarks>
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
        if (!_disposed || _readers != 0 || _retainCount != 0 || _compacting) return;

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
        DrainRetired();
        _inPlaceBytes = 0;
        _compressedMemory = 0;
        UpdateMetrics();
    }

    internal readonly ref struct ReadLease(TrieNodeLog log)
    {
        public void Dispose() => log.EndRead();
    }
}
