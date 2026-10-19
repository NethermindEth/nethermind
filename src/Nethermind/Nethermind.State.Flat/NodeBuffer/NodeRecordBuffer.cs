// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>An append-only buffer of trie node records, split into generations and addressed by 64-bit pointers.</summary>
/// <remarks>
/// A pointer is <c>(generationId &lt;&lt; 32) | byteOffset</c> and 0 means none. Generation ids start at 1 and are never reused,
/// and a lookup checks the id held by the generation's ring slot, so a stale pointer misses rather than resolving to another record.
/// The newest <see cref="LiveGenerations"/> generations are readable in place; older ones are dropped, except that a generation held by a
/// <see cref="NodeRecordBufferPin"/>, and every later generation, is never dropped.
/// Appends are lock-free bump reservations into the active generation; rotation, pins and drops run under a lock.
/// A generation's storage is compacted, evicted or freed only while no pin covers it, so a reader borrows native records for as
/// long as its <see cref="PinAll"/> lives.
/// </remarks>
public sealed partial class NodeRecordBuffer : INodeRecordBuffer
{
    /// <summary>The default size of a generation in bytes.</summary>
    public const int DefaultGenerationBytes = 64 * 1024 * 1024;

    private const int LiveGenerations = 8;
    private const int MaxGenerations = 4096;
    private const ulong GenerationSlotMask = MaxGenerations - 1;
    internal const int OffsetBits = NodeRecordCodec.OffsetBits;

    private readonly Lock _lock = new();
    private readonly Generation?[] _generations = new Generation?[MaxGenerations];
    private readonly int[] _pinCounts = new int[MaxGenerations];
    private readonly int _generationBytes;
    private Generation _active;
    private ulong _oldestLive = 1;
    private long _inPlaceBytes;

    /// <summary>Creates an empty buffer with generation 1 active.</summary>
    /// <param name="compressedMaxMemory">The memory budget in bytes for compacted generations; it also sizes the storage root index.</param>
    /// <param name="generationBytes">The size of a generation in bytes; a larger record gets a generation of its own size.</param>
    public NodeRecordBuffer(ulong compressedMaxMemory, int generationBytes)
    {
        _generationBytes = generationBytes;
        _compressedMaxMemory = compressedMaxMemory;
        _storageRoots = new ulong[BitOperations.RoundUpToPowerOf2(Math.Max(compressedMaxMemory / MemoryPerStorageRootSlot, StateRootSlots))];
        _active = StartGeneration(1, generationBytes);
        UpdateMetrics();
    }

    /// <summary>Keeps the active generation, and every later one, readable in place until the returned pin is disposed.</summary>
    public NodeRecordBufferPin Pin()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            ulong generation = _active.Id;
            long used = Math.Min(Volatile.Read(ref _active.Used), _active.Data!.Length);
            NodeRecordBufferPin pin = new(this, generation, generation << OffsetBits | (uint)used);
            _pinCount++;
            _pinCounts[generation & GenerationSlotMask]++;
            return pin;
        }
    }

    INodeRecordBufferPin INodeRecordBuffer.Pin() => Pin();

    /// <inheritdoc/>
    /// <remarks>Pins every generation that still holds storage, so none is dropped, compacted or evicted while the pin lives.</remarks>
    public IDisposable PinAll()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            for (ulong generation = _oldestCompressed; generation <= _active.Id; generation++) _pinCounts[generation & GenerationSlotMask]++;
            _pinCount++;
            return new AllGenerationsPin(this, _oldestCompressed, _active.Id);
        }
    }

    internal void AddPin(ulong generation)
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            if (generation < _oldestLive || generation > _active.Id) throw new ObjectDisposedException(nameof(NodeRecordBufferPin));
            _pinCount++;
            _pinCounts[generation & GenerationSlotMask]++;
        }
    }

    internal void RemovePin(ulong generation) => RemovePins(generation, generation);

    private void RemovePins(ulong oldest, ulong newest)
    {
        lock (_lock)
        {
            for (ulong generation = oldest; generation <= newest; generation++)
            {
                Debug.Assert(_pinCounts[generation & GenerationSlotMask] > 0, "Only a pinned generation can be released.");
                _pinCounts[generation & GenerationSlotMask]--;
            }
            _pinCount--;
            if (_disposed) TryCompleteDispose();
            else DropOldGenerations();
        }
    }

    internal bool TryLocate(ulong pointer, out NodeRecord record)
    {
        if (pointer == 0 || NodeRecordCodec.IsTransient(pointer))
        {
            record = default;
            return false;
        }
        ulong id = pointer >> OffsetBits;
        Generation? generation = Volatile.Read(ref _generations[id & GenerationSlotMask]);
        if (generation?.Id == id)
        {
            NativeNodeBuffer? data = Volatile.Read(ref generation.Data);
            if (data is not null)
            {
                record = new NodeRecord(data.Memory, (int)(uint)pointer);
                return true;
            }

            if (Volatile.Read(ref generation.Compressed)?.TryFind(pointer, out record) == true) return true;
        }

        record = default;
        return false;
    }

    /// <inheritdoc/>
    public NodeRecord GetRecord(ulong pointer)
    {
        ThrowIfUnavailable();
        return TryLocate(pointer, out NodeRecord record) ? record : ThrowNotLocatable(pointer);
    }

    bool INodeRecordBuffer.TryLocate(ulong pointer, out NodeRecord record) => TryLocate(pointer, out record);

    /// <inheritdoc/>
    public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => WriteBatch.Start(this, alignedTotalLength);

    /// <summary>Adds the node keys of the records written to <paramref name="bytes"/> to the bloom of their generation.</summary>
    private void CompleteWrite(ulong basePointer, ReadOnlySpan<byte> bytes)
    {
        BloomFilter? bloom = FindBloom(basePointer);
        if (bloom is null) return;
        for (int offset = 0; offset + NodeRecordCodec.HeaderLength <= bytes.Length;)
        {
            ReadOnlySpan<byte> record = bytes[offset..];
            int length = NodeRecordCodec.Length(record);
            // Past the last record a failed write leaves zeros, which read as a record without RLP.
            if (length == NodeRecordCodec.HeaderLength) break;
            bloom.Add(BinaryPrimitives.ReadUInt64LittleEndian(record[NodeRecordCodec.NodeKeyOffset..]));
            offset += NodeRecordCodec.AlignedLength(length);
        }
    }

    /// <summary>Reserves contiguous zero-filled bytes in the active generation.</summary>
    /// <remarks>The writer holds a pin no newer than the reservation until all reserved records are published.</remarks>
    /// <param name="alignedTotalLength">A multiple of <see cref="NodeRecordCodec.RecordAlignment"/>.</param>
    /// <returns>The pointer of the first reserved byte, which is at <paramref name="offset"/> in <paramref name="buffer"/>.</returns>
    internal ulong Reserve(int alignedTotalLength, out NativeNodeBuffer buffer, out int offset)
    {
        ThrowIfUnavailable();
        while (true)
        {
            Generation generation = Volatile.Read(ref _active);
            long end = Interlocked.Add(ref generation.Used, alignedTotalLength);
            NativeNodeBuffer? data = Volatile.Read(ref generation.Data);
            if (data is not null && end <= data.Length)
            {
                buffer = data;
                offset = (int)(end - alignedTotalLength);
                return generation.Id << OffsetBits | (uint)offset;
            }

            Rotate(generation.Id, alignedTotalLength);
        }
    }

    private void Rotate(ulong fullGeneration, int length)
    {
        lock (_lock)
        {
            if (_active.Id != fullGeneration) return;

            ulong id = NextGeneration(fullGeneration);
            EvictCompressed(id >= MaxGenerations ? id + 1 - MaxGenerations : 0);
            if (_generations[id & GenerationSlotMask] is { } reused && (reused.Data is not null || reused.Compressed is not null)) ThrowTooManyPinned();

            Volatile.Write(ref _active, StartGeneration(id, Math.Max(_generationBytes, length)));
            DropOldGenerations();
        }
    }

    internal static ulong NextGeneration(ulong current)
    {
        if (current >= NodeRecordCodec.TransientGenerationMask - 1) throw new TrieException("Trie node buffer generation IDs exhausted.");
        return current + 1;
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
        while (_oldestLive + LiveGenerations <= _active.Id && _pinCounts[_oldestLive & GenerationSlotMask] == 0)
        {
            _oldestLive++;
        }

        Reclaim();
        UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        Metrics.NodeRecordBufferMemory = _inPlaceBytes;
        Metrics.NodeRecordBufferGenerations = (long)(_active.Id - _oldestAllocated + 1);
        Metrics.NodeRecordBufferCompressedMemory = (long)_compressedMemory;
        Metrics.NodeRecordBufferCompressedGenerations = (long)(_oldestAllocated - _oldestCompressed);
    }

    [DoesNotReturn, StackTraceHidden]
    private static NodeRecord ThrowNotLocatable(ulong pointer) => throw new TrieException($"Trie node buffer pointer {pointer:x16} does not resolve to a record.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyPinned() => throw new TrieException("Too many pinned trie node buffer generations.");

    /// <remarks>Reused per thread once disposed, so starting a batch allocates nothing.</remarks>
    private sealed class WriteBatch : INodeRecordWriteBatch
    {
        [ThreadStatic] private static WriteBatch? _cached;

        private NodeRecordBuffer? _buffer;
        private NativeNodeBuffer? _data;
        private int _offset;
        private int _length;

        public ulong BasePointer { get; private set; }

        public Span<byte> Bytes => _data!.AsSpan(_offset, _length);

        public static WriteBatch Start(NodeRecordBuffer buffer, int alignedTotalLength)
        {
            ulong pointer = buffer.Reserve(alignedTotalLength, out NativeNodeBuffer data, out int offset);
            WriteBatch batch = _cached ?? new WriteBatch();
            _cached = null;
            batch._buffer = buffer;
            batch._data = data;
            batch._offset = offset;
            batch._length = alignedTotalLength;
            batch.BasePointer = pointer;
            return batch;
        }

        public void Dispose()
        {
            if (_buffer is not { } buffer) return;
            buffer.CompleteWrite(BasePointer, Bytes);
            _buffer = null;
            _data = null;
            _cached = this;
        }
    }

    private sealed class AllGenerationsPin(NodeRecordBuffer buffer, ulong oldest, ulong newest) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) buffer.RemovePins(oldest, newest);
        }
    }

    private sealed class Generation
    {
        public Generation(ulong id, int length)
        {
            Id = id;
            Data = NativeNodeBuffer.Allocate(length);
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
        public NativeNodeBuffer? Data;
        public CompressedGeneration? Compressed;
        public long Used;
    }
}
