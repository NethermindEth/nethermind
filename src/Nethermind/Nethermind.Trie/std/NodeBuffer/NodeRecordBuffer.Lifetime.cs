// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Trie.NodeBuffer;

public sealed partial class NodeRecordBuffer
{
    private readonly List<IDisposable> _retired = [];
    // Lookup-only references to owners already kept by _retired, keyed by full id rather than a reusable ring slot.
    private readonly Dictionary<ulong, RetiredGeneration> _retiredGenerations = [];
    private int _readers;
    private int _retainCount;
    private bool _disposed;
    private Exception? _compactionFailure;

    /// <summary>Protects borrowed records and lookup metadata retired during this operation until all overlapping readers finish.</summary>
    internal ReadLease BeginRead()
    {
        EnterRead();
        return new ReadLease(this);
    }

    /// <summary>Protects records across iterator yields and other operations that cannot hold a ref-struct lease.</summary>
    internal IDisposable BeginReadScope() => new ReadScope(this);

    private sealed class ReadScope : IDisposable
    {
        private NodeRecordBuffer? _buffer;

        public ReadScope(NodeRecordBuffer buffer)
        {
            buffer.EnterRead();
            _buffer = buffer;
        }

        public void Dispose() => Interlocked.Exchange(ref _buffer, null)?.EndRead();
    }

    private void EnterRead()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            _readers++;
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

    private void Retire(ulong generation, NativeNodeBuffer? data)
    {
        if (data is null) return;
        if (_readers > 0)
        {
            _retiredGenerations.TryGetValue(generation, out RetiredGeneration retired);
            retired.Data = data;
            _retiredGenerations[generation] = retired;
        }
        Retire(data);
    }

    private void Retire(ulong generation, CompressedGeneration? compressed)
    {
        if (compressed is null) return;
        if (_readers > 0)
        {
            _retiredGenerations.TryGetValue(generation, out RetiredGeneration retired);
            retired.Compressed = compressed;
            _retiredGenerations[generation] = retired;
        }
        Retire(compressed);
    }

    private bool TryLocateRetired(ulong pointer, out NodeRecord record)
    {
        lock (_lock)
        {
            if (_retiredGenerations.TryGetValue(pointer >> OffsetBits, out RetiredGeneration retired))
            {
                if (retired.Data is { } data)
                {
                    record = new NodeRecord(data.Memory, (int)(uint)pointer);
                    return true;
                }
                if (retired.Compressed?.TryFind(pointer, out record) == true) return true;
            }
            record = default;
            return false;
        }
    }

    private void DrainRetired()
    {
        _retiredGenerations.Clear();
        foreach (IDisposable owner in _retired) owner.Dispose();
        _retired.Clear();
    }

    private struct RetiredGeneration
    {
        public NativeNodeBuffer? Data;
        public CompressedGeneration? Compressed;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Volatile.Read(ref _compactionFailure) is { } failure)
            throw new TrieException("Trie node buffer compaction failed.", failure);
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

    internal readonly ref struct ReadLease(NodeRecordBuffer buffer)
    {
        public void Dispose() => buffer.EndRead();
    }
}
