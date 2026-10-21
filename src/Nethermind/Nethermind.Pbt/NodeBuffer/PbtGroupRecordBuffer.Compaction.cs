// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Memory;

namespace Nethermind.Pbt.NodeBuffer;

/// <remarks>
/// A dropped generation stays readable in place until it is compacted in the background, oldest first and one at a time.
/// Compaction keeps the last record of every path that no bloom filter of a later generation holds, since a later generation
/// then likely holds a newer group at the same path. Newer records link to the unchanged subtrees of older ones, so the records
/// kept are those likely still reachable from current roots. They keep their references and resolve through a compressed
/// generation, evicted oldest first while compressed generations take more than the compressed memory budget.
/// </remarks>
public sealed partial class PbtGroupRecordBuffer
{
    private const int BloomBytesPerKey = 128;
    private const double BloomBitsPerKey = 14;

    private readonly long _compressedMaxMemory;
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
            lock (_lock) return (int)(_oldestInPlace - _oldestCompressed);
        }
    }

    private static ulong PathKey(in PbtStorageNodePath path) => (ulong)(uint)path.GetHashCode() * 0x9E3779B97F4A7C15UL;

    private BloomFilter? CreateBloom(int length) =>
        _compressedMaxMemory > 0 ? new BloomFilter(Math.Max(1, length / BloomBytesPerKey), BloomBitsPerKey) : null;

    private void ScheduleCompaction()
    {
        if (_compacting || !IsCompactable) return;
        _compacting = true;
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(static buffer => buffer.CompactDropped(), this, preferLocal: false);
        }
        catch
        {
            _compacting = false;
            throw;
        }
    }

    private void CompactDropped()
    {
        try
        {
            while (true)
            {
                Generation generation;
                Generation[] later;
                lock (_lock)
                {
                    if (!CompactsDropped || !IsCompactable) return;
                    generation = _generations[_oldestInPlace];
                    later = new Generation[_lastGeneration - generation.Id];
                    for (int index = 0; index < later.Length; index++) later[index] = _generations[generation.Id + 1 + (uint)index];
                }

                // Only this compactor frees in-place generations while compaction is on, so the later ones keep their blooms.
                CompressedGeneration? compressed = Compress(generation, later);
                lock (_lock)
                {
                    if (_disposed)
                    {
                        compressed?.Dispose();
                        return;
                    }
                    Release(generation, compressed);
                    EvictCompressed();
                }
            }
        }
        catch (Exception exception)
        {
            // Reported by the next foreground call; dropped generations are then freed uncompacted.
            Volatile.Write(ref _compactionFailure, exception);
        }
        finally
        {
            lock (_lock)
            {
                _compacting = false;
                Trim(keepActive: false);
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
        // In buffer order, as the records of a tree were appended near each other.
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

    /// <summary>Evicts compacted generations, oldest first, while they take more than the budget or kept nothing.</summary>
    /// <remarks>Each one is marked first and evicted once the pins taken before the mark are released.</remarks>
    private void EvictCompressed()
    {
        long retainedBytes = _compressedBytes;
        for (uint id = _oldestCompressed; id < _oldestInPlace; id++)
        {
            Generation generation = _generations[id];
            CompressedGeneration? compressed = generation.Compressed;
            if (compressed is not null && retainedBytes <= _compressedMaxMemory) return;

            retainedBytes -= compressed?.Memory ?? 0;
            Volatile.Write(ref generation.Dropping, true);
            if (id != _oldestCompressed || generation.PinCount != 0) continue;

            _compressedBytes -= compressed?.Memory ?? 0;
            compressed?.Dispose();
            _generations.TryRemove(id, out _);
            _oldestCompressed++;
        }
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
