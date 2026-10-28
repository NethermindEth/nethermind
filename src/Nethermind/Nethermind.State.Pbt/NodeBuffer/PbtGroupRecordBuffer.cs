// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>An append-only buffer of PBT group records, split into generations and addressed by 64-bit pointers.</summary>
/// <remarks>
/// <para>
/// A pointer is <c>(generationId &lt;&lt; 32) | byteOffset</c> and 0 means none. Generation ids start at 1 and are never reused,
/// and a lookup checks the id held by the generation's ring slot, so a stale pointer misses rather than resolving to another record.
/// The newest <see cref="LiveGenerations"/> generations are readable in place; older ones are dropped, except that a generation held by a
/// <see cref="PbtGroupRecordBufferPin"/>, and every later generation, is never dropped.
/// Appends are lock-free bump reservations into the active generation; rotation, pins and drops run under a lock.
/// A generation's storage is compacted, evicted or freed only while no pin covers it, so a reader borrows native records for as
/// long as its <see cref="PinAll"/> lives.
/// A generation due to be dropped or evicted is first marked: it then misses for every lookup and later <see cref="PinAll"/> calls
/// skip it, so it waits only for the pins taken before the mark, however much later readers overlap.
/// Only the oldest consecutive generations of the compressed and of the in-place ones are marked, so a <see cref="PinAll"/> pins
/// two contiguous ranges.
/// </para>
/// <para>
/// A dropped generation is compacted in the background, oldest first and one at a time, and resolves again once compacted.
/// Compaction keeps every root key record, and the last record of every path key hash that no bloom filter of a later generation holds,
/// since a later generation then likely holds a newer group at the same position. Newer records link to the unchanged subtrees of older ones
/// by pointer, so the records kept are those likely still reachable from current roots. They keep their pointers and resolve through a
/// <see cref="PbtCompressedGeneration"/>, which is evicted oldest first while compressed generations take more than the compressed memory budget,
/// or once its ring slot is needed.
/// No generation is written once dropped, as a writer pins a generation no newer than those it writes, and no pinned generation is
/// compacted or evicted, as a reader pins every generation it may read.
/// </para>
/// <para>
/// The root index is a table of slots. A slot holds the pointer of the newest root key record hashing to it, and each root key record
/// links to the one it displaced, so colliding roots chain without locks or deletion.
/// A chain never links to a newer generation, so the first link that no longer resolves ends it.
/// Root key records live in the generations like group records and go away with them.
/// </para>
/// </remarks>
public sealed class PbtGroupRecordBuffer : IPbtGroupRecordBuffer
{
    /// <summary>The default size of a generation in bytes.</summary>
    public const int DefaultGenerationBytes = 64 * 1024 * 1024;

    /// <summary>How many of the newest generations stay readable in place.</summary>
    public const int LiveGenerations = 1;

    private const int MaxGenerations = 4096;
    private const ulong GenerationSlotMask = MaxGenerations - 1;
    public const int OffsetBits = PbtGroupRecordCodec.OffsetBits;

    private readonly Lock _lock = new();
    private readonly Generation?[] _generations = new Generation?[MaxGenerations];
    private readonly int _generationBytes;
    private Generation _active;
    private ulong _oldestLive = 1;
    private long _inPlaceBytes;

    private const int BloomBytesPerKey = 128;
    private const int BloomBatchSize = 64;
    private const int InitialKeptCapacity = 1024;

    private readonly ulong _compressedMaxMemory;
    private ulong _oldestAllocated = 1;
    private ulong _oldestCompressed = 1;
    private ulong _compressedMemory;

    // The next generation, allocated and faulted in the background, so a rotation on a writer only swaps it in.
    private Generation? _standby;
    private bool _preparingStandby;
    private bool _compacting;

    private const int RootSlots = 1024;

    // The newest root key record of each slot; older ones of the slot are chained through their previous pointers.
    private readonly ulong[] _rootIndex = new ulong[RootSlots];

    private int _pinCount;
    private bool _disposed;
    private Exception? _compactionFailure;

    /// <summary>Creates an empty buffer with generation 1 active.</summary>
    /// <param name="compressedMaxMemory">The memory budget in bytes for compacted generations.</param>
    /// <param name="generationBytes">The size of a generation in bytes; a larger record gets a generation of its own size.</param>
    public PbtGroupRecordBuffer(ulong compressedMaxMemory, int generationBytes)
    {
        _generationBytes = generationBytes;
        _compressedMaxMemory = compressedMaxMemory;
        _active = StartGeneration(1, Generation.Allocate(generationBytes));
        UpdateMetrics();
    }

    /// <summary>Keeps the active generation, and every later one, readable in place until the returned pin is disposed.</summary>
    public PbtGroupRecordBufferPin PinHead()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            ulong generation = _active.Id;
            long used = Math.Min(Volatile.Read(ref _active.Used), _active.Data!.Length);
            PbtGroupRecordBufferPin pin = new(this, generation, generation << OffsetBits | (uint)used);
            _pinCount++;
            _active.PinCount++;
            _active.HeadPinCount++;
            return pin;
        }
    }

    IPbtGroupRecordBufferPin IPbtGroupRecordBuffer.PinHead() => PinHead();

    /// <inheritdoc/>
    /// <remarks>
    /// Pins the compressed and the in-place generations from the first unmarked one of each, so none of them is dropped,
    /// compacted or evicted while the pin lives.
    /// </remarks>
    public IDisposable PinAll()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            ulong compressedFrom = PinFromFirstUnmarked(_oldestCompressed, _oldestAllocated);
            ulong inPlaceFrom = PinFromFirstUnmarked(_oldestAllocated, _active.Id + 1);
            _pinCount++;
            return new AllGenerationsPin(this, compressedFrom, _oldestAllocated, inPlaceFrom, _active.Id + 1);
        }
    }

    /// <summary>Pins the generations of [<paramref name="from"/>, <paramref name="to"/>) after its marked oldest ones.</summary>
    /// <returns>The first generation pinned.</returns>
    private ulong PinFromFirstUnmarked(ulong from, ulong to)
    {
        while (from < to && _generations[from & GenerationSlotMask]!.Dropping) from++;
        for (ulong id = from; id < to; id++) _generations[id & GenerationSlotMask]!.PinCount++;
        return from;
    }

    /// <summary>Pins <paramref name="generation"/> again for a copy of its <see cref="PbtGroupRecordBufferPin"/>.</summary>
    /// <exception cref="ObjectDisposedException">The buffer is disposed, or the generation is no longer held.</exception>
    public void AddPin(ulong generation)
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            if (generation < _oldestLive || generation > _active.Id || _generations[generation & GenerationSlotMask]!.Dropping)
                throw new ObjectDisposedException(nameof(PbtGroupRecordBufferPin));
            _pinCount++;
            _generations[generation & GenerationSlotMask]!.PinCount++;
            _generations[generation & GenerationSlotMask]!.HeadPinCount++;
        }
    }

    /// <summary>Releases one <see cref="PbtGroupRecordBufferPin"/> of <paramref name="generation"/>.</summary>
    public void RemovePin(ulong generation)
    {
        lock (_lock)
        {
            _generations[generation & GenerationSlotMask]!.HeadPinCount--;
            Unpin(generation, generation + 1);
            ReleasePin();
        }
    }

    private void RemovePins(ulong compressedFrom, ulong compressedTo, ulong inPlaceFrom, ulong inPlaceTo)
    {
        lock (_lock)
        {
            Unpin(compressedFrom, compressedTo);
            Unpin(inPlaceFrom, inPlaceTo);
            ReleasePin();
        }
    }

    private void Unpin(ulong from, ulong to)
    {
        for (ulong id = from; id < to; id++)
        {
            Debug.Assert(_generations[id & GenerationSlotMask]!.PinCount > 0, "Only a pinned generation can be released.");
            _generations[id & GenerationSlotMask]!.PinCount--;
        }
    }

    private void ReleasePin()
    {
        _pinCount--;
        if (_disposed) TryCompleteDispose();
        else DropOldGenerations();
    }

    /// <inheritdoc/>
    public bool TryLocate(ulong pointer, out PbtGroupRecord record)
    {
        if (pointer == 0 || PbtGroupRecordCodec.IsTransient(pointer))
        {
            record = default;
            return false;
        }
        ulong id = pointer >> OffsetBits;
        Generation? generation = Volatile.Read(ref _generations[id & GenerationSlotMask]);
        if (generation?.Id == id && !Volatile.Read(ref generation.Dropping))
        {
            PbtNativeGroupBuffer? data = Volatile.Read(ref generation.Data);
            if (data is not null)
            {
                record = new PbtGroupRecord(data.Memory, (int)(uint)pointer);
                return true;
            }

            if (Volatile.Read(ref generation.Compressed)?.TryFind(pointer, out record) == true) return true;
        }

        record = default;
        return false;
    }

    /// <inheritdoc/>
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength)
    {
        Metrics.PbtGroupRecordBufferWriteBatchBytes.Observe(alignedTotalLength);
        return WriteBatch.Start(this, alignedTotalLength);
    }

    /// <summary>Adds the path key hashes of the records reachable from <paramref name="rootPointer"/> at or past <paramref name="lowerBound"/> to the blooms of their generations.</summary>
    /// <remarks>The caller must hold a pin on the generations of those records.</remarks>
    public void AddToBlooms(ulong rootPointer, ulong lowerBound)
    {
        if (_compressedMaxMemory == 0) return;

        Span<long> pathKeyHashes = stackalloc long[BloomBatchSize];
        int count = 0;
        ulong generation = 0;
        using ArrayPoolListRef<ulong> pending = new(PbtThreeLevelGroupGeometry.BoundarySlots);
        pending.Add(rootPointer);
        while (pending.Count > 0)
        {
            ulong pointer = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (pointer == 0 || pointer < lowerBound || !TryLocate(pointer, out PbtGroupRecord record)) continue;

            if (count == BloomBatchSize || pointer >> OffsetBits != generation)
            {
                FindBloom(generation << OffsetBits)?.Add(pathKeyHashes[..count]);
                count = 0;
                generation = pointer >> OffsetBits;
            }

            pathKeyHashes[count++] = (long)MemoryMarshal.AsRef<PbtGroupRecordHeader>(record.Span).PathKeyHash;
            for (int slot = 0; slot < PbtThreeLevelGroupGeometry.BoundarySlots; slot++) pending.Add(record.GetLink(slot));
        }

        FindBloom(generation << OffsetBits)?.Add(pathKeyHashes[..count]);
    }

    /// <summary>Reserves contiguous zero-filled bytes in the active generation.</summary>
    /// <remarks>The writer holds a pin no newer than the reservation until all reserved records are published.</remarks>
    /// <param name="alignedTotalLength">A multiple of <see cref="PbtGroupRecordCodec.RecordAlignment"/>.</param>
    /// <returns>The pointer of the first reserved byte, which is at <paramref name="offset"/> in <paramref name="buffer"/>.</returns>
    private ulong Reserve(int alignedTotalLength, out PbtNativeGroupBuffer buffer, out int offset)
    {
        ThrowIfUnavailable();
        while (true)
        {
            Generation generation = Volatile.Read(ref _active);
            long end = Interlocked.Add(ref generation.Used, alignedTotalLength);
            PbtNativeGroupBuffer? data = Volatile.Read(ref generation.Data);
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

            Volatile.Write(ref _active, StartGeneration(id, TakeStandby(length) ?? Generation.Allocate(Math.Max(_generationBytes, length))));
            DropOldGenerations();
            // A dropped generation awaiting compaction becomes the standby once compacted, so only allocate one without.
            if (_oldestAllocated == _oldestLive) PrepareStandby(null);
        }
    }

    /// <summary>The generation id after <paramref name="current"/>.</summary>
    /// <exception cref="InvalidOperationException">Generation ids are exhausted.</exception>
    public static ulong NextGeneration(ulong current)
    {
        if (current >= PbtGroupRecordCodec.TransientGenerationMask - 1) throw new InvalidOperationException("PBT group record buffer generation IDs exhausted.");
        return current + 1;
    }

    private Generation StartGeneration(ulong id, Generation generation)
    {
        generation.Id = id;
        Volatile.Write(ref _generations[id & GenerationSlotMask], generation);
        _inPlaceBytes += generation.Data!.Length;
        return generation;
    }

    /// <returns>The standby generation when it fits a record of <paramref name="length"/> bytes, or null to allocate one inline.</returns>
    private Generation? TakeStandby(int length)
    {
        Generation? standby = _standby;
        if (standby is null || length > standby.Data!.Length) return null;

        _standby = null;
        return standby;
    }

    /// <summary>Prepares the standby in the background from <paramref name="recycled"/>, or from a new allocation when null, unless one is held or being prepared.</summary>
    /// <remarks>Only buffers that rotate get a standby, so a buffer that never fills its first generation commits no second one.</remarks>
    /// <param name="recycled">The storage of a retired generation of the base size, which no pin covers.</param>
    /// <returns>Whether the standby is prepared from <paramref name="recycled"/> or a new allocation.</returns>
    private bool PrepareStandby(Generation? recycled)
    {
        if (_standby is not null || _preparingStandby || _disposed) return false;

        _preparingStandby = true;
        ThreadPool.UnsafeQueueUserWorkItem(static state => state.Buffer.FillStandby(state.Recycled), (Buffer: this, Recycled: recycled), preferLocal: false);
        return true;
    }

    private void FillStandby(Generation? recycled)
    {
        Generation? generation = recycled;
        try
        {
            if (generation is null)
            {
                generation = Generation.Allocate(_generationBytes);
                generation.Data!.Prefault();
            }
            else
            {
                // Clearing makes every page resident, as the prefault of a new allocation does.
                generation.Data!.GetSpan().Clear();
                generation.Bloom!.Clear();
            }
        }
        catch
        {
            // A failed allocation leaves no standby, so the next rotation allocates inline and reports the failure to its writer.
            generation?.Free();
            generation = null;
        }

        lock (_lock)
        {
            _preparingStandby = false;
            if (_disposed || generation is null) generation?.Free();
            else _standby = generation;
        }
    }

    private void DropOldGenerations()
    {
        for (ulong id = _oldestLive; id + LiveGenerations <= _active.Id; id++)
        {
            Generation generation = _generations[id & GenerationSlotMask]!;
            if (generation.HeadPinCount != 0) break;

            Volatile.Write(ref generation.Dropping, true);
            if (id == _oldestLive && generation.PinCount == 0) _oldestLive++;
        }

        Reclaim();
        UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        Metrics.PbtGroupRecordBufferMemory = _inPlaceBytes;
        Metrics.PbtGroupRecordBufferGenerations = (long)(_active.Id - _oldestAllocated + 1);
        Metrics.PbtGroupRecordBufferCompressedMemory = (long)_compressedMemory;
        Metrics.PbtGroupRecordBufferCompressedGenerations = (long)(_oldestAllocated - _oldestCompressed);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyPinned() => throw new InvalidOperationException("Too many pinned PBT group record buffer generations.");

    /// <summary>The bloom filter of the path key hashes written to the generation of <paramref name="pointer"/>, or null once it is compacted.</summary>
    /// <remarks>The caller must hold a pin for the entire use of the filter.</remarks>
    private BloomFilter? FindBloom(ulong pointer)
    {
        ulong id = pointer >> OffsetBits;
        Generation? generation = Volatile.Read(ref _generations[id & GenerationSlotMask]);
        return generation?.Id == id ? Volatile.Read(ref generation.Bloom) : null;
    }

    private bool IsCompactable => _oldestAllocated < _oldestLive && _generations[_oldestAllocated & GenerationSlotMask]!.PinCount == 0;

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

                PbtCompressedGeneration? compressed = Compress(generation, newest);
                try
                {
                    lock (_lock)
                    {
                        if (_disposed) return;
                        if (compressed is not null)
                        {
                            _compressedMemory += (ulong)compressed.Memory;
                            Volatile.Write(ref generation.Compressed, compressed);
                            compressed = null;
                        }

                        _inPlaceBytes -= generation.Data!.Length;
                        if (generation.Data.Length != _generationBytes || !PrepareStandby(new Generation(generation.Data, generation.Bloom!)))
                        {
                            Retire(generation.Data);
                            Retire(generation.Bloom);
                            // A storage the standby cannot take still leaves a standby to prepare.
                            PrepareStandby(null);
                        }
                        Volatile.Write(ref generation.Data, null);
                        Volatile.Write(ref generation.Bloom, null);
                        Volatile.Write(ref generation.Dropping, false);
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
    private PbtCompressedGeneration? Compress(Generation generation, ulong newest)
    {
        PbtNativeGroupBuffer data = generation.Data!;
        // Reservations that did not fit leave Used past the end.
        long end = Math.Min(Volatile.Read(ref generation.Used), data.Length);
        using ArrayPoolList<int> kept = new(InitialKeptCapacity);
        Dictionary<ulong, int> lastOffsetByPathKeyHash = [];
        for (int offset = 0; offset + PbtGroupRecordCodec.HeaderLength <= end;)
        {
            ReadOnlySpan<byte> record = data.AsSpan(offset);
            int length = PbtGroupRecordCodec.Length(record);
            if ((PbtGroupRecordKind)record[0] == PbtGroupRecordKind.RootKey) kept.Add(offset);
            // Past the last record the generation is zero-filled, which reads as a group record without a payload.
            else if (length == PbtGroupRecordCodec.HeaderLength) break;
            else lastOffsetByPathKeyHash[MemoryMarshal.AsRef<PbtGroupRecordHeader>(record).PathKeyHash] = offset;

            offset += PbtGroupRecordCodec.AlignedLength(length);
        }

        foreach ((ulong pathKeyHash, int offset) in lastOffsetByPathKeyHash)
        {
            if (!IsWrittenAfter(generation.Id, newest, pathKeyHash)) kept.Add(offset);
        }

        if (kept.Count == 0) return null;

        // In buffer order, as the records of a tree were appended near each other.
        kept.AsSpan().Sort();
        return new PbtCompressedGeneration(data, generation.Id, kept.AsSpan());
    }

    private bool IsWrittenAfter(ulong id, ulong newest, ulong pathKeyHash)
    {
        // Only this compactor retires blooms; later generations keep theirs until this pass finishes.
        for (ulong later = id + 1; later <= newest; later++)
        {
            if (_generations[later & GenerationSlotMask]!.Bloom!.MightContain(pathKeyHash)) return true;
        }

        return false;
    }

    /// <summary>Evicts compressed generations, oldest first, while they take more than the budget, are older than <paramref name="before"/>, or kept nothing.</summary>
    /// <remarks>Each one is marked first and evicted once the pins taken before the mark are released.</remarks>
    private void EvictCompressed(ulong before)
    {
        ulong retainedMemory = _compressedMemory;
        for (ulong id = _oldestCompressed; id < _oldestAllocated; id++)
        {
            Generation generation = _generations[id & GenerationSlotMask]!;
            PbtCompressedGeneration? compressed = generation.Compressed;
            if (compressed is not null && retainedMemory <= _compressedMaxMemory && id >= before) return;

            retainedMemory -= (ulong)(compressed?.Memory ?? 0);
            Volatile.Write(ref generation.Dropping, true);
            if (id != _oldestCompressed || generation.PinCount != 0) continue;

            _compressedMemory -= (ulong)(compressed?.Memory ?? 0);
            Retire(compressed);
            Volatile.Write(ref generation.Compressed, null);
            _oldestCompressed++;
        }
    }

    /// <summary>The record pointer of the newest indexed <paramref name="root"/>, or 0 when it is not indexed or its record no longer resolves.</summary>
    public ulong FindRoot(in ValueHash256 root)
    {
        ulong pointer = Volatile.Read(ref RootSlot(root));
        while (TryLocate(pointer, out PbtGroupRecord rootKey))
        {
            ReadOnlySpan<byte> record = rootKey.Span;
            if (PbtRootKeyRecordCodec.Matches(record, root))
            {
                ulong rootPointer = PbtRootKeyRecordCodec.RootPointer(record);
                return TryLocate(rootPointer, out _) ? rootPointer : 0;
            }

            pointer = PbtRootKeyRecordCodec.Previous(record);
        }

        return 0;
    }

    /// <summary>Indexes <paramref name="rootPointer"/> as the record of <paramref name="root"/>, ahead of any earlier entry of the same root.</summary>
    public void SetRoot(in ValueHash256 root, ulong rootPointer)
    {
        ref ulong slot = ref RootSlot(root);
        ulong previous = Volatile.Read(ref slot);
        while (true)
        {
            ulong rootKey = Reserve(PbtGroupRecordCodec.RootKeyLength, out PbtNativeGroupBuffer buffer, out int offset);
            PbtRootKeyRecordCodec.Write(buffer.AsSpan(offset, PbtGroupRecordCodec.RootKeyLength), root, rootPointer, previous);

            // A lost race appends a fresh record instead of relinking this one: the winner may sit in a newer generation.
            ulong current = Interlocked.CompareExchange(ref slot, rootKey, previous);
            if (current == previous) return;

            previous = current;
        }
    }

    private ref ulong RootSlot(in ValueHash256 root) => ref _rootIndex[MemoryMarshal.Read<ulong>(root.Bytes) & (RootSlots - 1)];

    /// <remarks>
    /// Only storage of a generation no pin covers is retired, so no reader can still borrow it. It is freed in the background, as
    /// unmapping a generation is slow and the retiring thread, which holds the lock, is often a writer or the block thread.
    /// </remarks>
    private static void Retire(IDisposable? owner)
    {
        if (owner is not null) ThreadPool.UnsafeQueueUserWorkItem(static owner => owner.Dispose(), owner, preferLocal: false);
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Volatile.Read(ref _compactionFailure) is { } failure)
            throw new InvalidOperationException("PBT group record buffer compaction failed.", failure);
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

        // Freed at once, as nothing is left to stall.
        foreach (Generation? generation in _generations)
        {
            if (generation is null) continue;
            generation.Free();
            generation.Compressed?.Dispose();
            generation.Compressed = null;
        }
        _standby?.Free();
        _standby = null;
        _inPlaceBytes = 0;
        _compressedMemory = 0;
        UpdateMetrics();
    }

    /// <remarks>Reused per thread once disposed, so starting a batch allocates nothing.</remarks>
    private sealed class WriteBatch : IPbtGroupRecordWriteBatch
    {
        [ThreadStatic] private static WriteBatch? _cached;

        private PbtGroupRecordBuffer? _buffer;
        private PbtNativeGroupBuffer? _data;
        private int _offset;
        private int _length;

        public ulong BasePointer { get; private set; }

        public Span<byte> Bytes => _data!.AsSpan(_offset, _length);

        public static WriteBatch Start(PbtGroupRecordBuffer buffer, int alignedTotalLength)
        {
            ulong pointer = buffer.Reserve(alignedTotalLength, out PbtNativeGroupBuffer data, out int offset);
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
            if (_buffer is null) return;
            _buffer = null;
            _data = null;
            _cached = this;
        }
    }

    private sealed class AllGenerationsPin(PbtGroupRecordBuffer buffer, ulong compressedFrom, ulong compressedTo, ulong inPlaceFrom, ulong inPlaceTo) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) buffer.RemovePins(compressedFrom, compressedTo, inPlaceFrom, inPlaceTo);
        }
    }

    /// <remarks>The id is set when the generation becomes active.</remarks>
    private sealed class Generation(PbtNativeGroupBuffer data, BloomFilter bloom)
    {
        public static Generation Allocate(int length)
        {
            PbtNativeGroupBuffer data = PbtNativeGroupBuffer.Allocate(length);
            try
            {
                return new Generation(data, new BloomFilter(Math.Max(1, length / BloomBytesPerKey), 14));
            }
            catch
            {
                ((IDisposable)data).Dispose();
                throw;
            }
        }

        public ulong Id;
        public BloomFilter? Bloom = bloom;
        public PbtNativeGroupBuffer? Data = data;
        public PbtCompressedGeneration? Compressed;
        public long Used;
        public int PinCount;
        // The pins of a PbtGroupRecordBufferPin, which also keeps every later generation.
        public int HeadPinCount;
        public bool Dropping;

        public void Free()
        {
            ((IDisposable?)Data)?.Dispose();
            Bloom?.Dispose();
            Data = null;
            Bloom = null;
        }
    }
}
