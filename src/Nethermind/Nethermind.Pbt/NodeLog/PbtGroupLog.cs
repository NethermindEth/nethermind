// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Memory;

namespace Nethermind.Pbt.NodeLog;

/// <summary>An append-only in-memory log of canonical PBT groups and direct descendant references.</summary>
/// <remarks>
/// One operation lease protects all borrowed records and concurrent writers in a fold. Generations are reclaimed only
/// between operations. Snapshot pins keep their generation and every later generation readable in place. Reads and
/// reservations do not take the lifetime lock; only rotation and pin changes do. The memory provider is borrowed, not owned.
/// </remarks>
public sealed partial class PbtGroupLog : IDisposable
{
    /// <summary>The default generation capacity, in bytes.</summary>
    public const int DefaultGenerationBytes = 1024 * 1024;

    private const int RecordAlignment = sizeof(ulong);
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<uint, Generation> _generations = new();
    private readonly SortedList<uint, int> _pins = [];
    private readonly IRefCountingMemoryProvider _memoryProvider;
    private readonly int _generationBytes;
    private readonly int _retainedGenerations;
    private Generation? _active;
    private uint _lastGeneration;
    private long _residentBytes;
    private int _operations;
    private bool _disposed;

    /// <summary>Creates a log using the selected native or managed memory provider.</summary>
    /// <param name="memoryProvider">Allocates generation buffers and remains owned by the caller.</param>
    /// <param name="generationBytes">Requested generation capacity in bytes; a larger record gets a generation large enough for it.</param>
    /// <param name="retainedGenerations">Number of newest generations to keep opportunistically, in addition to snapshot pins.</param>
    public PbtGroupLog(IRefCountingMemoryProvider memoryProvider, int generationBytes = DefaultGenerationBytes, int retainedGenerations = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generationBytes, PbtGroupRecordHeader.Size + 1);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedGenerations);
        _memoryProvider = memoryProvider;
        _generationBytes = generationBytes;
        _retainedGenerations = retainedGenerations;
    }

    /// <summary>Capacity in bytes of currently allocated generations, including metadata and unused space.</summary>
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
                    if (id >= oldestPinned && !KeepOpportunistically(generation)) bytes += generation.Buffer.Capacity;
                return bytes;
            }
        }
    }

    /// <summary>Reserved bytes in resident generations, including record metadata and alignment.</summary>
    public long UsedBytes
    {
        get
        {
            lock (_lock)
            {
                long bytes = 0;
                foreach ((_, Generation generation) in _generations) bytes += Volatile.Read(ref generation.Used);
                return bytes;
            }
        }
    }

    /// <summary>The number of resident generations, for retention diagnostics.</summary>
    public int GenerationCount => _generations.Count;

    /// <summary>Reserves aligned, contiguous log space for a group record and exposes its borrowed writable payload.</summary>
    /// <returns>The record's generation and offset reference.</returns>
    /// <remarks>
    /// Only the header is initialized. The caller must fill <paramref name="payload"/> before handing the reference to a parent,
    /// journal, or owner, and keep the operation lease alive while using the span.
    /// </remarks>
    public PbtGroupReference Reserve(OperationLease operation, PbtStorageNodePath path, int payloadLength, in PbtGroupLinks links, out Span<byte> payload)
    {
        operation.EnsureActive(this);
        ThrowIfDisposed();
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
                    payload = record.Span[PbtGroupRecordHeader.Size..];
                    return new PbtGroupReference(generation.Id, offset);
                }
            }
            Rotate(generation, alignedLength);
        }
    }

    /// <summary>Copies an existing canonical group into a new immutable record.</summary>
    public PbtGroupReference Append(OperationLease operation, PbtStorageNodePath path, ReadOnlySpan<byte> payload, in PbtGroupLinks links)
    {
        PbtGroupReference reference = Reserve(operation, path, payload.Length, links, out Span<byte> destination);
        payload.CopyTo(destination);
        return reference;
    }

    /// <summary>Resolves a direct reference without searching snapshot layers.</summary>
    /// <remarks>A miss is resolved through the caller's persisted view. The operation must outlive all uses of the returned view.</remarks>
    public bool TryGetRecord(OperationLease operation, PbtGroupReference reference, out PbtGroupRecord record)
    {
        operation.EnsureActive(this);
        record = default;
        if (!TryLocate(reference, out ReadOnlyMemory<byte> memory)) return false;
        record = new PbtGroupRecord(reference, memory);
        return true;
    }

    private bool TryLocate(PbtGroupReference reference, out ReadOnlyMemory<byte> record)
    {
        record = default;
        if (!reference.IsDirect || !_generations.TryGetValue(reference.Generation, out Generation? generation)) return false;
        int offset = reference.Offset;
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
            ThrowIfDisposed();
            if (!ReferenceEquals(_active, expected)) return;
            if (_lastGeneration == uint.MaxValue) throw new InvalidOperationException("The PBT group log exhausted its generation ids.");
            RefCountingMemory buffer = _memoryProvider.Rent(Math.Max(_generationBytes, recordLength));
            Generation generation = new(++_lastGeneration, buffer);
            _generations[generation.Id] = generation;
            Interlocked.Add(ref _residentBytes, buffer.Capacity);
            Volatile.Write(ref _active, generation);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

    private sealed class Generation(uint id, RefCountingMemory buffer)
    {
        public uint Id { get; } = id;
        public RefCountingMemory Buffer { get; } = buffer;
        public Memory<byte> Memory { get; } = buffer.Memory;
        public int Used;
    }
}
