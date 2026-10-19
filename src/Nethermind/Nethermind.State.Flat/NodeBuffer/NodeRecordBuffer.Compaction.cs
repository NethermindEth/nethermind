// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <remarks>
/// A dropped generation stays readable in place until it is compacted in the background, oldest first and one at a time.
/// Compaction keeps every root key record, and the last record of every node key hash that no bloom filter of a later generation holds,
/// since a later generation then likely holds a newer node at the same position. Newer records reference the unchanged subtrees of older ones
/// by pointer, so the records kept are those likely still reachable from current roots. They keep their pointers and resolve through a
/// <see cref="CompressedGeneration"/>, which is evicted oldest first while compressed generations take more than the compressed memory budget,
/// or once its ring slot is needed.
/// No generation is written once dropped, as a writer pins a generation no newer than those it writes, and no pinned generation is
/// compacted or evicted, as a reader pins every generation it may read.
/// </remarks>
public sealed partial class NodeRecordBuffer
{
    private const int BloomBytesPerKey = 128;
    private const int InitialKeptCapacity = 1024;

    private readonly ulong _compressedMaxMemory;
    private ulong _oldestAllocated = 1;
    private ulong _oldestCompressed = 1;
    private ulong _compressedMemory;
    private bool _compacting;

    /// <summary>The bloom filter of the node key hashes written to the generation of <paramref name="pointer"/>, or null once it is compacted.</summary>
    /// <remarks>The caller must hold a pin for the entire use of the filter.</remarks>
    internal BloomFilter? FindBloom(ulong pointer)
    {
        ulong id = pointer >> OffsetBits;
        Generation? generation = Volatile.Read(ref _generations[id & GenerationSlotMask]);
        return generation?.Id == id ? Volatile.Read(ref generation.Bloom) : null;
    }

    private bool IsCompactable => _oldestAllocated < _oldestLive && _pinCounts[_oldestAllocated & GenerationSlotMask] == 0;

    private void Reclaim()
    {
        EvictCompressed(0);
        if (_disposed || _compactionFailure is not null || _compacting || !IsCompactable) return;

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
                ulong newest;
                lock (_lock)
                {
                    if (_disposed || !IsCompactable) return;
                    generation = _generations[_oldestAllocated & GenerationSlotMask]!;
                    newest = _active.Id;
                }

                CompressedGeneration? compressed = Compress(generation, newest);
                try
                {
                    lock (_lock)
                    {
                        // A reader pinned it while it was being compressed; compaction resumes once the reader releases it.
                        if (_disposed || _pinCounts[generation.Id & GenerationSlotMask] != 0) return;
                        if (compressed is not null)
                        {
                            _compressedMemory += (ulong)compressed.Memory;
                            Volatile.Write(ref generation.Compressed, compressed);
                            compressed = null;
                        }

                        _inPlaceBytes -= generation.Data!.Length;
                        Retire(generation.Data);
                        Volatile.Write(ref generation.Data, null);
                        Retire(generation.Bloom);
                        Volatile.Write(ref generation.Bloom, null);
                        _oldestAllocated++;
                        EvictCompressed(0);
                        UpdateMetrics();
                    }
                }
                finally
                {
                    compressed?.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            // Report background failure on the next foreground operation, without losing native owners.
            Volatile.Write(ref _compactionFailure, exception);
        }
        finally
        {
            lock (_lock)
            {
                _compacting = false;
                if (_disposed) TryCompleteDispose();
                else Reclaim();
            }
        }
    }

    /// <returns>What compaction keeps of <paramref name="generation"/>, or null when it keeps nothing.</returns>
    private CompressedGeneration? Compress(Generation generation, ulong newest)
    {
        NativeNodeBuffer data = generation.Data!;
        // Reservations that did not fit leave Used past the end.
        long end = Math.Min(Volatile.Read(ref generation.Used), data.Length);
        using ArrayPoolList<int> kept = new(InitialKeptCapacity);
        Dictionary<ulong, int> lastOffsetByNodeKeyHash = [];
        for (int offset = 0; offset + NodeRecordCodec.HeaderLength <= end;)
        {
            ReadOnlySpan<byte> record = data.AsSpan(offset);
            int length = NodeRecordCodec.Length(record);
            if ((NodeRecordKind)record[0] == NodeRecordKind.RootKey) kept.Add(offset);
            // Past the last record the generation is zero-filled, which reads as a record without RLP.
            else if (length == NodeRecordCodec.HeaderLength) break;
            else lastOffsetByNodeKeyHash[BinaryPrimitives.ReadUInt64LittleEndian(record[NodeRecordCodec.NodeKeyHashOffset..])] = offset;

            offset += NodeRecordCodec.AlignedLength(length);
        }

        foreach ((ulong nodeKeyHash, int offset) in lastOffsetByNodeKeyHash)
        {
            if (!IsWrittenAfter(generation.Id, newest, nodeKeyHash)) kept.Add(offset);
        }

        if (kept.Count == 0) return null;

        // In buffer order, as the records of a trie were appended near each other.
        kept.AsSpan().Sort();
        return new CompressedGeneration(data, generation.Id, kept.AsSpan());
    }

    private bool IsWrittenAfter(ulong id, ulong newest, ulong nodeKeyHash)
    {
        // Only this compactor retires blooms; later generations keep theirs until this pass finishes.
        for (ulong later = id + 1; later <= newest; later++)
        {
            if (_generations[later & GenerationSlotMask]!.Bloom!.MightContain(nodeKeyHash)) return true;
        }

        return false;
    }

    /// <summary>Evicts compressed generations, oldest first, while they take more than the budget, are older than <paramref name="before"/>, or kept nothing.</summary>
    private void EvictCompressed(ulong before)
    {
        while (_oldestCompressed < _oldestAllocated)
        {
            if (_pinCounts[_oldestCompressed & GenerationSlotMask] != 0) return;
            Generation generation = _generations[_oldestCompressed & GenerationSlotMask]!;
            CompressedGeneration? compressed = generation.Compressed;
            if (compressed is not null && _compressedMemory <= _compressedMaxMemory && _oldestCompressed >= before) return;

            _compressedMemory -= (ulong)(compressed?.Memory ?? 0);
            Retire(compressed);
            Volatile.Write(ref generation.Compressed, null);
            _oldestCompressed++;
        }
    }
}
