// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

/// <summary>An append-only log of trie node records, split into generations and addressed by 64-bit pointers.</summary>
/// <remarks>
/// A pointer is <c>(generationId &lt;&lt; 32) | byteOffset</c> and 0 means none. Generation ids start at 1 and are never reused,
/// and a lookup checks the id held by the generation's ring slot, so a stale pointer misses rather than resolving to another record.
/// The newest <see cref="LiveGenerations"/> generations are readable in place; older ones are dropped, except that a generation held by a
/// <see cref="TrieNodeLogRetain"/>, and every later generation, is never dropped.
/// Appends are lock-free bump reservations into the active generation; rotation, retains and drops run under a lock.
/// Readers borrow native records within an operation lease or generation retain; retired storage is freed after readers finish.
/// </remarks>
public sealed partial class TrieNodeLog : IDisposable
{
    /// <summary>The default size of a generation in bytes.</summary>
    public const int DefaultGenerationBytes = 64 * 1024 * 1024;

    private const int LiveGenerations = 8;
    private const int MaxGenerations = 4096;
    private const ulong GenerationSlotMask = MaxGenerations - 1;
    internal const int OffsetBits = 32;

    private readonly Lock _lock = new();
    private readonly Generation?[] _generations = new Generation?[MaxGenerations];
    private readonly int[] _retains = new int[MaxGenerations];
    private readonly int _generationBytes;
    private Generation _active;
    private ulong _oldestLive = 1;
    private long _inPlaceBytes;

    /// <summary>Creates an empty log with generation 1 active.</summary>
    /// <param name="compressedMaxMemory">The memory budget in bytes for compacted generations; it also sizes the storage root index.</param>
    /// <param name="generationBytes">The size of a generation in bytes; a larger record gets a generation of its own size.</param>
    public TrieNodeLog(ulong compressedMaxMemory, int generationBytes)
    {
        _generationBytes = generationBytes;
        _compressedMaxMemory = compressedMaxMemory;
        _storageRoots = new ulong[BitOperations.RoundUpToPowerOf2(Math.Max(compressedMaxMemory / MemoryPerStorageRootSlot, StateRootSlots))];
        _active = StartGeneration(1, generationBytes);
        UpdateMetrics();
    }

    /// <summary>Keeps the active generation, and every later one, readable in place until the returned retain is disposed.</summary>
    public TrieNodeLogRetain RetainActive()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            ulong generation = _active.Id;
            long used = Math.Min(Volatile.Read(ref _active.Used), _active.Data!.Length);
            TrieNodeLogRetain retain = new(this, generation, generation << OffsetBits | (uint)used);
            _retainCount++;
            _retains[generation & GenerationSlotMask]++;
            return retain;
        }
    }

    internal void Retain(ulong generation)
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            if (generation < _oldestLive || generation > _active.Id) throw new ObjectDisposedException(nameof(TrieNodeLogRetain));
            _retainCount++;
            _retains[generation & GenerationSlotMask]++;
        }
    }

    internal void Release(ulong generation)
    {
        lock (_lock)
        {
            Debug.Assert(_retains[generation & GenerationSlotMask] > 0, "Only a retained generation can be released.");
            _retains[generation & GenerationSlotMask]--;
            _retainCount--;
            if (_disposed) TryCompleteDispose();
            else DropOldGenerations();
        }
    }

    internal bool TryLocate(ulong pointer, out NodeRecordRef record)
    {
        if (pointer == 0)
        {
            record = default;
            return false;
        }
        ulong id = pointer >> OffsetBits;
        Generation? generation = Volatile.Read(ref _generations[id & GenerationSlotMask]);
        if (generation?.Id != id) return TryLocateRetired(pointer, out record);

        LogBuffer? data = Volatile.Read(ref generation.Data);
        if (data is not null)
        {
            record = new NodeRecordRef(data.Memory, (int)(uint)pointer);
            return true;
        }

        CompressedGeneration? compressed = Volatile.Read(ref generation.Compressed);
        if (compressed?.TryFind(pointer, out record) == true) return true;
        return TryLocateRetired(pointer, out record);
    }

    /// <summary>The record at <paramref name="pointer"/>.</summary>
    /// <remarks>The caller must hold an operation lease or a <see cref="TrieNodeLogRetain"/> on this generation or an older one for the entire use of the record, including its spans and references.</remarks>
    /// <exception cref="TrieException">The pointer does not resolve to a record.</exception>
    public NodeRecordRef GetRecord(ulong pointer)
    {
        ThrowIfUnavailable();
        return TryLocate(pointer, out NodeRecordRef record) ? record : ThrowNotLocatable(pointer);
    }

    /// <summary>Reserves contiguous zero-filled bytes in the active generation.</summary>
    /// <remarks>The writer holds a generation retain no newer than the reservation until all reserved records are published.</remarks>
    /// <param name="alignedTotalLength">A multiple of <see cref="NodeRecord.RecordAlignment"/>.</param>
    /// <returns>The pointer of the first reserved byte, which is at <paramref name="offset"/> in <paramref name="buffer"/>.</returns>
    internal ulong Reserve(int alignedTotalLength, out LogBuffer buffer, out int offset)
    {
        ThrowIfUnavailable();
        while (true)
        {
            Generation generation = Volatile.Read(ref _active);
            long end = Interlocked.Add(ref generation.Used, alignedTotalLength);
            LogBuffer? data = Volatile.Read(ref generation.Data);
            if (data is not null && end <= data.Length)
            {
                buffer = data;
                offset = (int)(end - alignedTotalLength);
                return generation.Id << OffsetBits | (uint)offset;
            }

            Rotate(generation.Id, alignedTotalLength);
        }
    }

    /// <summary>Appends the record of a node given by its RLP, with every child pointer 0.</summary>
    /// <exception cref="TrieException">The RLP is not a valid trie node.</exception>
    internal ulong AppendRecordFromRlp(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        using ReadLease lease = BeginRead();
        if (!NodeRecord.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask)) ThrowInvalidRlp();

        int length = NodeRecord.Length(BitOperations.PopCount(hashMask), rlp.Length);
        ulong pointer = Reserve(NodeRecord.AlignedLength(length), out LogBuffer buffer, out int offset);
        ulong nodeKey = NodeRecord.NodeKey(address, path);
        NodeRecord.WriteFromRlp(rlp, kind, hashMask, hash, nodeKey, buffer.AsSpan(offset, length));
        FindBloom(pointer)?.Add(nodeKey);
        return pointer;
    }

    private void Rotate(ulong fullGeneration, int length)
    {
        lock (_lock)
        {
            if (_active.Id != fullGeneration) return;

            ulong id = fullGeneration + 1;
            EvictCompressed(id >= MaxGenerations ? id + 1 - MaxGenerations : 0);
            if (_generations[id & GenerationSlotMask] is { } reused && (reused.Data is not null || reused.Compressed is not null)) ThrowTooManyRetained();

            Volatile.Write(ref _active, StartGeneration(id, Math.Max(_generationBytes, length)));
            DropOldGenerations();
        }
    }

    private Generation StartGeneration(ulong id, int length)
    {
        Generation generation = new(id, length);
        Volatile.Write(ref _generations[id & GenerationSlotMask], generation);
        _inPlaceBytes += length;
        return generation;
    }

    private void DropOldGenerations()
    {
        while (_oldestLive + LiveGenerations <= _active.Id && _retains[_oldestLive & GenerationSlotMask] == 0)
        {
            _oldestLive++;
        }

        Reclaim();
        UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        Metrics.TrieNodeLogMemory = _inPlaceBytes;
        Metrics.TrieNodeLogGenerations = (long)(_active.Id - _oldestAllocated + 1);
        Metrics.TrieNodeLogCompressedMemory = (long)_compressedMemory;
        Metrics.TrieNodeLogCompressedGenerations = (long)(_oldestAllocated - _oldestCompressed);
    }

    [DoesNotReturn, StackTraceHidden]
    private static NodeRecordRef ThrowNotLocatable(ulong pointer) => throw new TrieException($"Trie node log pointer {pointer:x16} does not resolve to a record.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidRlp() => throw new TrieException("Invalid trie node RLP.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyRetained() => throw new TrieException("Too many retained trie node log generations.");

    private sealed class Generation
    {
        public Generation(ulong id, int length)
        {
            Id = id;
            Data = LogBuffer.Allocate(length);
            try
            {
                Bloom = new BloomFilter(Math.Max(1, length / BloomBytesPerKey), 14);
            }
            catch
            {
                ((IDisposable)Data).Dispose();
                throw;
            }
        }

        public readonly ulong Id;
        public BloomFilter? Bloom;
        public LogBuffer? Data;
        public CompressedGeneration? Compressed;
        public long Used;
    }
}
