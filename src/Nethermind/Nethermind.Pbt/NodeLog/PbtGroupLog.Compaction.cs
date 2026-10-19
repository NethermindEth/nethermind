// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Memory;

namespace Nethermind.Pbt.NodeLog;

/// <remarks>
/// A dropped generation stays readable in place until it is compacted in the background, oldest first and one at a time.
/// Compaction keeps the last record of every path that no bloom filter of a later generation holds, since a later generation
/// then likely holds a newer group at the same path. Newer records link to the unchanged subtrees of older ones, so the records
/// kept are those likely still reachable from current roots. They keep their references and resolve through a compressed
/// generation, evicted oldest first while compressed generations take more than the compressed memory budget.
/// </remarks>
public sealed partial class PbtGroupLog
{
    private const int BloomBytesPerKey = 128;
    private const double BloomBitsPerKey = 14;

    private readonly long _compressedMemoryBudget;
    private readonly Queue<Generation> _compressed = new();
    private long _compressedBytes;
    private bool _compacting;
    private Exception? _compactionFailure;

    /// <summary>Bytes held by compacted generations, within the compressed memory budget.</summary>
    public long CompressedBytes
    {
        get
        {
            lock (_lock) return _compressedBytes;
        }
    }

    /// <summary>The number of compacted generations still resolving their kept records.</summary>
    public int CompressedGenerationCount
    {
        get
        {
            lock (_lock) return _compressed.Count;
        }
    }

    private static ulong PathKey(in PbtStorageNodePath path) => (ulong)(uint)path.GetHashCode() * 0x9E3779B97F4A7C15UL;

    private BloomFilter? CreateBloom(int length) =>
        _compressedMemoryBudget > 0 ? new BloomFilter(Math.Max(1, length / BloomBytesPerKey), BloomBitsPerKey) : null;

    private void ScheduleCompaction(ulong oldestOperation)
    {
        if (_compacting || NextCompactable(oldestOperation) is null) return;
        _compacting = true;
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(static log => log.CompactDropped(), this, preferLocal: false);
        }
        catch
        {
            _compacting = false;
            throw;
        }
    }

    private Generation? NextCompactable(ulong oldestOperation)
    {
        Generation? next = null;
        foreach (Generation generation in _dropped)
            if (generation.DroppedAt < oldestOperation && (next is null || generation.Id < next.Id)) next = generation;
        return next;
    }

    private void CompactDropped()
    {
        try
        {
            while (true)
            {
                Generation generation;
                Generation[] later;
                OperationLease reading;
                lock (_lock)
                {
                    if (!CompactsDropped || NextCompactable(OldestOperationEpoch) is not { } next) return;
                    generation = next;
                    List<Generation> newer = [];
                    foreach ((uint id, Generation candidate) in _generations)
                        if (id > generation.Id && candidate.Bloom is not null) newer.Add(candidate);
                    later = [.. newer];
                    reading = BeginOperation();
                }
                using (reading)
                {
                    CompressedGeneration? compressed = Compress(generation, later);
                    lock (_lock)
                    {
                        if (!CompactsDropped)
                        {
                            compressed?.Dispose();
                            return;
                        }
                        ulong epoch = _epoch++;
                        _dropped.Remove(generation);
                        Interlocked.Add(ref _residentBytes, -generation.Buffer!.Capacity);
                        Retire(epoch, generation.Buffer);
                        Retire(epoch, generation.Bloom);
                        if (compressed is null) _generations.TryRemove(generation.Id, out _);
                        else
                        {
                            Volatile.Write(ref generation.Compressed, compressed);
                            _compressed.Enqueue(generation);
                            _compressedBytes += compressed.Memory;
                        }
                        Volatile.Write(ref generation.Buffer, null);
                        generation.Bloom = null;
                        EvictCompressed();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // Reported by the next foreground call; the dropped generation is then unlinked uncompacted.
            Volatile.Write(ref _compactionFailure, exception);
        }
        finally
        {
            lock (_lock)
            {
                _compacting = false;
                Trim();
            }
        }
    }

    /// <returns>What compaction keeps of <paramref name="generation"/>, or null when it keeps nothing.</returns>
    private CompressedGeneration? Compress(Generation generation, Generation[] later)
    {
        ReadOnlyMemory<byte> memory = generation.Memory;
        int end = Volatile.Read(ref generation.Used);
        Dictionary<PbtStorageNodePath, int> lastOffsetByPath = [];
        for (int offset = 0; offset + PbtGroupRecordHeader.Size <= end;)
        {
            ref readonly PbtGroupRecordHeader header = ref MemoryMarshal.AsRef<PbtGroupRecordHeader>(memory.Span[offset..]);
            lastOffsetByPath[header.Path] = offset;
            offset += AlignedLength(header.PayloadLength);
        }

        List<int> kept = [];
        foreach ((PbtStorageNodePath path, int offset) in lastOffsetByPath)
            if (!IsWrittenAfter(later, PathKey(path))) kept.Add(offset);
        if (kept.Count == 0) return null;
        // In log order, as the records of a tree were appended near each other.
        kept.Sort();
        return new CompressedGeneration(_memoryProvider, generation.Id, memory.Span, kept);
    }

    private static bool IsWrittenAfter(Generation[] later, ulong pathKey)
    {
        foreach (Generation generation in later)
            if (generation.Bloom!.MightContain(pathKey)) return true;
        return false;
    }

    private static int AlignedLength(int payloadLength) => (PbtGroupRecordHeader.Size + payloadLength + RecordAlignment - 1) & -RecordAlignment;

    /// <summary>Unlinks compacted generations, oldest first, while they take more than the budget or the log is disposed.</summary>
    private void EvictCompressed()
    {
        while (_compressed.Count > 0 && (_disposed || _compressedBytes > _compressedMemoryBudget)) Unlink(_compressed.Dequeue());
    }

    /// <summary>The records compaction kept of a generation, found by their original reference.</summary>
    /// <remarks>An open-addressing table with linear probing and a load factor of at most 0.75; immutable once built, so lookups take no lock.</remarks>
    private sealed class CompressedGeneration : IDisposable
    {
        private readonly RefCountingMemory _records;
        private readonly (int Offset, int CopiedOffset)[] _table;
        private readonly uint _generation;
        private readonly int _tableMask;

        public CompressedGeneration(IRefCountingMemoryProvider memoryProvider, uint generation, ReadOnlySpan<byte> source, List<int> keptOffsets)
        {
            _generation = generation;
            int length = 0;
            foreach (int offset in keptOffsets) length += AlignedLength(PayloadLength(source, offset));
            _table = new (int, int)[BitOperations.RoundUpToPowerOf2((uint)(keptOffsets.Count * 4 / 3 + 1))];
            _table.AsSpan().Fill((-1, 0));
            _tableMask = _table.Length - 1;
            _records = memoryProvider.Rent(length);
            Span<byte> destination = _records.GetSpan();
            int copied = 0;
            foreach (int offset in keptOffsets)
            {
                int recordLength = PbtGroupRecordHeader.Size + PayloadLength(source, offset);
                int index = Index(offset);
                while (_table[index].Offset != -1) index = (index + 1) & _tableMask;
                _table[index] = (offset, copied);
                source.Slice(offset, recordLength).CopyTo(destination[copied..]);
                copied += AlignedLength(PayloadLength(source, offset));
            }
        }

        /// <summary>Bytes held by the records and the table.</summary>
        public long Memory => _records.Capacity + (long)_table.Length * Unsafe.SizeOf<(int, int)>();

        public bool TryFind(PbtGroupReference reference, out ReadOnlyMemory<byte> record)
        {
            if (reference.Generation == _generation)
                for (int index = Index(reference.Offset); _table[index].Offset != -1; index = (index + 1) & _tableMask)
                {
                    if (_table[index].Offset != reference.Offset) continue;
                    ReadOnlyMemory<byte> records = _records.Memory;
                    int copied = _table[index].CopiedOffset;
                    record = records.Slice(copied, PbtGroupRecordHeader.Size + PayloadLength(records.Span, copied));
                    return true;
                }
            record = default;
            return false;
        }

        public void Dispose() => ((IDisposable)_records).Dispose();

        private int Index(int offset) => (int)(((uint)offset * 0x9E3779B9u) >> 7) & _tableMask;

        private static int PayloadLength(ReadOnlySpan<byte> records, int offset) => MemoryMarshal.AsRef<PbtGroupRecordHeader>(records[offset..]).PayloadLength;
    }
}
