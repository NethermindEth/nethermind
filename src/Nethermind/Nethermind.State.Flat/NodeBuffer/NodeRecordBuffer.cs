// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>An append-only buffer of trie node records, split into generations and addressed by 64-bit pointers.</summary>
/// <remarks>
/// <para>
/// A pointer is <c>(generationId &lt;&lt; 32) | byteOffset</c> and 0 means none. Generation ids start at 1 and are never reused,
/// and a lookup checks the id held by the generation's ring slot, so a stale pointer misses rather than resolving to another record.
/// The newest <see cref="LiveGenerations"/> generations are readable in place; older ones are dropped, except that a generation held by a
/// <see cref="NodeRecordBufferPin"/>, and every later generation, is never dropped.
/// Appends are lock-free bump reservations into the active generation; rotation, pins and drops run under a lock.
/// A generation's storage is freed only while no pin covers it, so a reader borrows native records for as long as its <see cref="PinAll"/> lives.
/// A generation due to be dropped is first marked: it then misses for every lookup and later <see cref="PinAll"/> calls skip it, so it
/// waits only for the pins taken before the mark, however much later readers overlap.
/// Only the oldest consecutive generations are marked, so a <see cref="PinAll"/> pins one contiguous range.
/// No generation is written once dropped, as a writer pins a generation no newer than those it writes, and no pinned generation is
/// freed, as a reader pins every generation it may read.
/// </para>
/// <para>
/// The root index is two tables of slots, one for the state trie and one for storage tries. A slot holds the pointer of the newest root key
/// record hashing to it, and each root key record links to the one it displaced, so colliding roots chain without locks or deletion.
/// A chain never links to a newer generation, so the first link that no longer resolves ends it.
/// Root key records live in the generations like node records and go away with them.
/// </para>
/// </remarks>
public sealed class NodeRecordBuffer : INodeRecordBuffer
{
    /// <summary>The default size of a generation in bytes.</summary>
    public const int DefaultGenerationBytes = 64 * 1024 * 1024;

    /// <summary>How many of the newest generations stay readable in place.</summary>
    public const int LiveGenerations = 1;

    private const int MaxGenerations = 4096;
    private const ulong GenerationSlotMask = MaxGenerations - 1;
    internal const int OffsetBits = NodeRecordCodec.OffsetBits;

    private readonly Lock _lock = new();
    private readonly Generation?[] _generations = new Generation?[MaxGenerations];
    private readonly int _generationBytes;
    private Generation _active;
    private ulong _oldestLive = 1;
    private long _inPlaceBytes;

    // The next generation, allocated and faulted in the background, so a rotation on a writer only swaps it in.
    private Generation? _standby;
    private bool _preparingStandby;

    private const int StateRootSlots = 1024;
    private const int StorageRootSlots = 128 * 1024;

    // The newest root key record of each slot; older ones of the slot are chained through their previous pointers.
    private readonly ulong[] _stateRootIndex = new ulong[StateRootSlots];
    private readonly ulong[] _storageRootIndex = new ulong[StorageRootSlots];

    private int _pinCount;
    private bool _disposed;

    /// <summary>Creates an empty buffer with generation 1 active.</summary>
    /// <param name="generationBytes">The size of a generation in bytes; a larger record gets a generation of its own size.</param>
    public NodeRecordBuffer(int generationBytes)
    {
        _generationBytes = generationBytes;
        _active = StartGeneration(1, new Generation(generationBytes));
        UpdateMetrics();
    }

    /// <summary>Keeps the active generation, and every later one, readable in place until the returned pin is disposed.</summary>
    public NodeRecordBufferPin PinHead()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            ulong generation = _active.Id;
            long used = Math.Min(Volatile.Read(ref _active.Used), _active.Data!.Length);
            NodeRecordBufferPin pin = new(this, generation, generation << OffsetBits | (uint)used);
            _pinCount++;
            _active.PinCount++;
            _active.HeadPinCount++;
            return pin;
        }
    }

    INodeRecordBufferPin INodeRecordBuffer.PinHead() => PinHead();

    /// <inheritdoc/>
    /// <remarks>
    /// Pins the generations from the first unmarked one, so none of them is dropped or freed while the pin lives.
    /// </remarks>
    public IDisposable PinAll()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            ulong from = PinFromFirstUnmarked(_oldestLive, _active.Id + 1);
            _pinCount++;
            return new AllGenerationsPin(this, from, _active.Id + 1);
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

    internal void AddPin(ulong generation)
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            if (generation < _oldestLive || generation > _active.Id || _generations[generation & GenerationSlotMask]!.Dropping)
                throw new ObjectDisposedException(nameof(NodeRecordBufferPin));
            _pinCount++;
            _generations[generation & GenerationSlotMask]!.PinCount++;
            _generations[generation & GenerationSlotMask]!.HeadPinCount++;
        }
    }

    internal void RemovePin(ulong generation)
    {
        lock (_lock)
        {
            _generations[generation & GenerationSlotMask]!.HeadPinCount--;
            Unpin(generation, generation + 1);
            ReleasePin();
        }
    }

    private void RemovePins(ulong from, ulong to)
    {
        lock (_lock)
        {
            Unpin(from, to);
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
    public bool TryLocate(ulong pointer, out NodeRecord record)
    {
        if (pointer == 0 || NodeRecordCodec.IsTransient(pointer))
        {
            record = default;
            return false;
        }
        ulong id = pointer >> OffsetBits;
        Generation? generation = Volatile.Read(ref _generations[id & GenerationSlotMask]);
        if (generation?.Id == id && !Volatile.Read(ref generation.Dropping))
        {
            NativeNodeBuffer? data = Volatile.Read(ref generation.Data);
            if (data is not null)
            {
                record = new NodeRecord(data.Memory, (int)(uint)pointer);
                return true;
            }
        }

        record = default;
        return false;
    }

    /// <inheritdoc/>
    public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => WriteBatch.Start(this, alignedTotalLength);

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
            if (id >= MaxGenerations && id - MaxGenerations >= _oldestLive) ThrowTooManyPinned();

            Volatile.Write(ref _active, StartGeneration(id, TakeStandby(length) ?? new Generation(Math.Max(_generationBytes, length))));
            PrepareStandby();
            DropOldGenerations();
        }
    }

    internal static ulong NextGeneration(ulong current)
    {
        if (current >= NodeRecordCodec.TransientGenerationMask - 1) throw new TrieException("Trie node buffer generation IDs exhausted.");
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

    /// <remarks>Only buffers that rotate get a standby, so a buffer that never fills its first generation commits no second one.</remarks>
    private void PrepareStandby()
    {
        if (_standby is not null || _preparingStandby || _disposed) return;

        _preparingStandby = true;
        ThreadPool.UnsafeQueueUserWorkItem(static buffer => buffer.AllocateStandby(), this, preferLocal: false);
    }

    private void AllocateStandby()
    {
        Generation? generation = null;
        try
        {
            generation = new Generation(_generationBytes);
            generation.Data!.Prefault();
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
            if (id != _oldestLive || generation.PinCount != 0) continue;

            _inPlaceBytes -= generation.Data!.Length;
            Retire(generation.Data);
            Volatile.Write(ref generation.Data, null);
            _oldestLive++;
        }

        UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        Metrics.NodeRecordBufferMemory = _inPlaceBytes;
        Metrics.NodeRecordBufferGenerations = (long)(_active.Id - _oldestLive + 1);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyPinned() => throw new TrieException("Too many pinned trie node buffer generations.");

    /// <summary>The record pointer of the newest <paramref name="root"/> of the trie indexed below <paramref name="boundary"/>, or 0 when there is none or its record no longer resolves.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <param name="boundary">Root key records at or past this pointer are skipped, as by a reader pinned there; <see cref="ulong.MaxValue"/> for none.</param>
    internal ulong FindRoot(Hash256? address, in ValueHash256 root, ulong boundary)
    {
        ValueHash256 addressHash = address?.ValueHash256 ?? default;
        ulong pointer = Volatile.Read(ref RootSlot(address, root));
        while (TryLocate(pointer, out NodeRecord rootKey))
        {
            ReadOnlySpan<byte> record = rootKey.Span;
            if (pointer < boundary && RootKeyRecordCodec.Matches(record, addressHash, root))
            {
                ulong rootPointer = RootKeyRecordCodec.RootPointer(record);
                return TryLocate(rootPointer, out _) ? rootPointer : 0;
            }

            pointer = RootKeyRecordCodec.Previous(record);
        }

        return 0;
    }

    /// <summary>Indexes <paramref name="rootPointer"/> as the record of <paramref name="root"/>, ahead of any earlier entry of the same root.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    internal void SetRoot(Hash256? address, in ValueHash256 root, ulong rootPointer)
    {
        ref ulong slot = ref RootSlot(address, root);
        ulong previous = Volatile.Read(ref slot);
        while (true)
        {
            ulong rootKey = Reserve(NodeRecordCodec.RootKeyLength, out NativeNodeBuffer buffer, out int offset);
            RootKeyRecordCodec.Write(buffer.AsSpan(offset, NodeRecordCodec.RootKeyLength), address, root, rootPointer, previous);

            // A lost race appends a fresh record instead of relinking this one: the winner may sit in a newer generation.
            ulong current = Interlocked.CompareExchange(ref slot, rootKey, previous);
            if (current == previous) return;

            previous = current;
        }
    }

    ulong INodeRecordBuffer.FindRoot(Hash256? address, in ValueHash256 root, ulong boundary) => FindRoot(address, root, boundary);

    void INodeRecordBuffer.SetRoot(Hash256? address, in ValueHash256 root, ulong rootPointer) => SetRoot(address, root, rootPointer);

    private ref ulong RootSlot(Hash256? address, in ValueHash256 root)
    {
        ulong[] slots = address is null ? _stateRootIndex : _storageRootIndex;
        ulong addressWord = address is null ? 0 : MemoryMarshal.Read<ulong>(address.Bytes);
        return ref slots[(MemoryMarshal.Read<ulong>(root.Bytes) ^ addressWord) & (ulong)(slots.Length - 1)];
    }

    /// <remarks>
    /// Only storage of a generation no pin covers is retired, so no reader can still borrow it. It is freed in the background, as
    /// unmapping a generation is slow and the retiring thread, which holds the lock, is often a writer or the block thread.
    /// </remarks>
    private static void Retire(IDisposable? owner)
    {
        if (owner is not null) ThreadPool.UnsafeQueueUserWorkItem(static owner => owner.Dispose(), owner, preferLocal: false);
    }

    private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

    /// <summary>Stops new operations and releases native storage once outstanding pins are released.</summary>
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
        if (!_disposed || _pinCount != 0) return;

        // Freed at once, as nothing is left to stall.
        foreach (Generation? generation in _generations) generation?.Free();
        _standby?.Free();
        _standby = null;
        _inPlaceBytes = 0;
        UpdateMetrics();
    }

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
            if (_buffer is null) return;
            _buffer = null;
            _data = null;
            _cached = this;
        }
    }

    private sealed class AllGenerationsPin(NodeRecordBuffer buffer, ulong from, ulong to) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) buffer.RemovePins(from, to);
        }
    }

    /// <remarks>The id is set when the generation becomes active.</remarks>
    private sealed class Generation(int length)
    {
        public ulong Id;
        public NativeNodeBuffer? Data = NativeNodeBuffer.Allocate(length);
        public long Used;
        public int PinCount;
        // The pins of a NodeRecordBufferPin, which also keeps every later generation.
        public int HeadPinCount;
        public bool Dropping;

        public void Free()
        {
            ((IDisposable?)Data)?.Dispose();
            Data = null;
        }
    }
}
