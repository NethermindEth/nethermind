// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Memory;

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>An append-only in-memory buffer of canonical PBT groups and descendant pointers, split into generations and addressed by 64-bit pointers.</summary>
/// <remarks>
/// A pointer is <c>(generationId &lt;&lt; 32) | byteOffset</c> and 0 means none. Generation ids start at 1 and are never reused, so a
/// stale pointer misses rather than resolving to another record.
/// A writer holds a <see cref="PinHead"/> no newer than the generations it writes, which keeps that generation and every later one
/// readable in place. A reader holds a <see cref="PinAll"/> taken before it looks up the records it borrows. Other generations
/// are dropped, compacted and reclaimed once no pin covers them. Reads and reservations do not take the lifetime lock; only
/// rotation and pin changes do. The memory provider is borrowed, not owned.
/// </remarks>
public sealed partial class PbtGroupRecordBuffer : IPbtGroupRecordBuffer, IDisposable
{
    /// <summary>The default generation capacity, in bytes.</summary>
    public const int DefaultGenerationBytes = 1024 * 1024;

    /// <summary>The bits of a pointer that hold the byte offset in its generation.</summary>
    public const int OffsetBits = 32;

    private const int RecordAlignment = sizeof(ulong);
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<uint, Generation> _generations = new();
    private readonly SortedList<uint, int> _pins = [];
    private readonly IRefCountingMemoryProvider _memoryProvider;
    private readonly int _generationBytes;
    private readonly int _liveGenerations;
    private Generation? _active;
    private uint _lastGeneration;
    private long _residentBytes;
    private bool _disposed;

    /// <summary>Creates a buffer using the selected native or managed memory provider.</summary>
    /// <param name="memoryProvider">Allocates generation buffers and remains owned by the caller.</param>
    /// <param name="compressedMaxMemory">Bytes compacted generations may keep; zero reclaims dropped generations without compacting them.</param>
    /// <param name="generationBytes">Requested generation capacity in bytes; a larger record gets a generation large enough for it.</param>
    /// <param name="liveGenerations">Number of newest generations to keep opportunistically, in addition to snapshot pins.</param>
    public PbtGroupRecordBuffer(IRefCountingMemoryProvider memoryProvider, long compressedMaxMemory, int generationBytes = DefaultGenerationBytes, int liveGenerations = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generationBytes, PbtGroupRecordHeader.Size + 1);
        ArgumentOutOfRangeException.ThrowIfNegative(liveGenerations);
        ArgumentOutOfRangeException.ThrowIfNegative(compressedMaxMemory);
        _memoryProvider = memoryProvider;
        _compressedMaxMemory = compressedMaxMemory;
        _generationBytes = generationBytes;
        _liveGenerations = liveGenerations;
    }

    /// <summary>Capacity in bytes of generations held in place, including metadata and unused space.</summary>
    public long ResidentBytes => Interlocked.Read(ref _residentBytes);

    /// <summary>Capacity in bytes kept by snapshot pins beyond the opportunistic generation window.</summary>
    public long PinnedBytes
    {
        get
        {
            lock (_lock)
            {
                long bytes = 0;
                uint oldestPinned = OldestPinnedGeneration;
                foreach ((uint id, Generation generation) in _generations)
                    if (id >= oldestPinned && !KeepOpportunistically(id)) bytes += generation.Buffer?.Capacity ?? 0;
                return bytes;
            }
        }
    }

    /// <summary>Reserved bytes in generations held in place, including record metadata and alignment.</summary>
    public long UsedBytes
    {
        get
        {
            lock (_lock)
            {
                long bytes = 0;
                foreach ((_, Generation generation) in _generations)
                    if (generation.Buffer is not null) bytes += Volatile.Read(ref generation.Used);
                return bytes;
            }
        }
    }

    /// <summary>The number of resident generations, in place or compacted, for retention diagnostics.</summary>
    public int GenerationCount => _generations.Count;

    /// <inheritdoc/>
    public ulong Reserve(PbtStorageNodePath path, int payloadLength, in PbtGroupLinks links, out Span<byte> payload)
    {
        ThrowIfUnavailable();
        if (!PbtThreeLevelGroupGeometry.IsGroupDepth(path.BitDepth)) throw new ArgumentException("The path is not a PBT group boundary.", nameof(path));
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadLength, PbtNodeGroupCodec.MaxPayloadLength);
        int recordLength = PbtGroupRecordHeader.Size + payloadLength;
        int alignedLength = (recordLength + RecordAlignment - 1) & -RecordAlignment;
        while (true)
        {
            Generation? generation = Volatile.Read(ref _active);
            if (generation is not null)
            {
                int offset = Volatile.Read(ref generation.Used);
                if (alignedLength <= generation.Memory.Length - offset)
                {
                    if (Interlocked.CompareExchange(ref generation.Used, offset + alignedLength, offset) != offset) continue;
                    Memory<byte> record = generation.Memory.Slice(offset, recordLength);
                    PbtGroupRecordHeader header = new() { PayloadLength = payloadLength, Path = path, Links = links };
                    MemoryMarshal.Write(record.Span, in header);
                    generation.Bloom?.Add(PathKey(path));
                    payload = record.Span[PbtGroupRecordHeader.Size..];
                    return (ulong)generation.Id << OffsetBits | (uint)offset;
                }
            }
            Rotate(generation, alignedLength);
        }
    }

    /// <inheritdoc/>
    public bool TryLocate(ulong pointer, out PbtGroupRecord record)
    {
        record = default;
        if (!TryLocateBytes(pointer, out ReadOnlyMemory<byte> memory)) return false;
        record = new PbtGroupRecord(pointer, memory);
        return true;
    }

    private bool TryLocateBytes(ulong pointer, out ReadOnlyMemory<byte> record)
    {
        record = default;
        if (pointer == 0 || !_generations.TryGetValue((uint)(pointer >> OffsetBits), out Generation? generation) || Volatile.Read(ref generation.Dropping)) return false;
        // A compacted generation publishes its copy before it unlinks its buffer.
        if (Volatile.Read(ref generation.Buffer) is null) return Volatile.Read(ref generation.Compressed)?.TryFind(pointer, out record) == true;
        int offset = (int)(uint)pointer;
        if ((uint)offset > (uint)(generation.Memory.Length - PbtGroupRecordHeader.Size)) return false;
        ReadOnlySpan<byte> remaining = generation.Memory.Span[offset..];
        ref readonly PbtGroupRecordHeader header = ref MemoryMarshal.AsRef<PbtGroupRecordHeader>(remaining);
        record = generation.Memory.Slice(offset, PbtGroupRecordHeader.Size + header.PayloadLength);
        return true;
    }

    private void Rotate(Generation? expected, int recordLength)
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            if (!ReferenceEquals(_active, expected)) return;
            if (_lastGeneration == uint.MaxValue) throw new InvalidOperationException("The PBT group record buffer exhausted its generation ids.");
            RefCountingMemory buffer = _memoryProvider.Rent(Math.Max(_generationBytes, recordLength));
            Generation generation = new(++_lastGeneration, buffer, CreateBloom(buffer.Capacity));
            _generations[generation.Id] = generation;
            Interlocked.Add(ref _residentBytes, buffer.Capacity);
            Volatile.Write(ref _active, generation);
            Trim(keepActive: true);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

    private sealed class Generation(uint id, RefCountingMemory buffer, BloomFilter? bloom)
    {
        public uint Id { get; } = id;
        public Memory<byte> Memory { get; } = buffer.Memory;
        public int Used;
        // Null once compacted, when Compressed holds what compaction kept.
        public RefCountingMemory? Buffer = buffer;
        // The paths reserved here, which later compactions of older generations consult; null when the buffer does not compact.
        public BloomFilter? Bloom = bloom;
        public CompressedGeneration? Compressed;
        // The PinAll pins covering the generation; the head pins of GenerationPin are counted in _pins.
        public int PinCount;
        // Set once the generation is due to be compacted or evicted: lookups then miss it and PinAll skips it.
        public bool Dropping;
    }
}
