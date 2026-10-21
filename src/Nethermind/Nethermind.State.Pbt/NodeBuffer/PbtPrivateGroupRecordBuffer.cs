// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>An append-only buffer of PBT group records that keeps every record until it is disposed.</summary>
/// <remarks>
/// Pointers are laid out as in <see cref="PbtGroupRecordBuffer"/>, but no generation is ever dropped or compacted, so pins only defer
/// the release of native storage past <see cref="Dispose"/>. It reports no metrics and keeps roots in a plain dictionary.
/// The first generation is allocated on the first write and its storage is kept across <see cref="Clear"/>.
/// </remarks>
/// <param name="generationBytes">The size of a generation in bytes; a larger record gets a generation of its own size.</param>
public sealed class PbtPrivateGroupRecordBuffer(int generationBytes) : IPbtGroupRecordBuffer
{
    private const int InitialGenerationSlots = 16;

    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<ValueHash256, ulong> _roots = new();
    private Generation?[] _generations = new Generation?[InitialGenerationSlots];
    private ulong _firstGeneration = 1;
    private Generation? _active;
    private int _pinCount;
    private bool _disposed;

    /// <inheritdoc/>
    public IPbtGroupRecordBufferPin PinHead()
    {
        lock (_lock)
        {
            AddPin();
            if (_active is null) return new BufferPin(this, _firstGeneration << PbtGroupRecordBuffer.OffsetBits);

            long used = Math.Min(Volatile.Read(ref _active.Used), _active.Data.Length);
            return new BufferPin(this, _active.Id << PbtGroupRecordBuffer.OffsetBits | (uint)used);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Unused: readers only take <see cref="IPbtGroupRecordBuffer.PinAll"/> on the global buffer.</remarks>
    public IDisposable PinAll() => PinHead();

    /// <inheritdoc/>
    public bool TryLocate(ulong pointer, out PbtGroupRecord record)
    {
        ulong index = (pointer >> PbtGroupRecordBuffer.OffsetBits) - _firstGeneration;
        Generation?[] generations = Volatile.Read(ref _generations);
        if (index < (ulong)generations.Length && Volatile.Read(ref generations[index]) is { } generation)
        {
            record = new PbtGroupRecord(generation.Data.Memory, (int)(uint)pointer);
            return true;
        }

        record = default;
        return false;
    }

    /// <inheritdoc/>
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        while (true)
        {
            Generation? generation = Volatile.Read(ref _active);
            if (generation is not null)
            {
                long end = Interlocked.Add(ref generation.Used, alignedTotalLength);
                if (end <= generation.Data.Length)
                {
                    int offset = (int)(end - alignedTotalLength);
                    return new WriteBatch(generation.Id << PbtGroupRecordBuffer.OffsetBits | (uint)offset, generation.Data, offset, alignedTotalLength);
                }
            }

            lock (_lock)
            {
                // The first generation keeps the base size even for a larger record, so Clear can always reuse it.
                if (_active != generation) continue;
                if (generation is null) StartGeneration(_firstGeneration, PbtNativeGroupBuffer.Allocate(generationBytes));
                else StartGeneration(PbtGroupRecordBuffer.NextGeneration(generation.Id), PbtNativeGroupBuffer.Allocate(Math.Max(generationBytes, alignedTotalLength)));
            }
        }
    }

    /// <inheritdoc/>
    public ulong FindRoot(in ValueHash256 root) => _roots.GetValueOrDefault(root);

    /// <inheritdoc/>
    public void SetRoot(in ValueHash256 root, ulong rootPointer) => _roots[root] = rootPointer;

    /// <summary>Frees every record and root; later records get pointers past the freed ones.</summary>
    /// <remarks>No pin may be held, and no record or pointer handed out before may be used after.</remarks>
    public void Clear()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _roots.Clear();
            if (_active is null) return;

            ulong next = PbtGroupRecordBuffer.NextGeneration(_active.Id);
            // The first generation always has the base size, so its storage is reused rather than reallocated.
            // It does not need to be zeroed: a record write covers every byte the record is read back by.
            PbtNativeGroupBuffer reused = _generations[0]!.Data;
            _generations[0] = null;
            FreeGenerations();
            _firstGeneration = next;
            StartGeneration(next, reused);
        }
    }

    /// <summary>Stops new operations; native storage is released once every pin is.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            if (_pinCount == 0) FreeGenerations();
        }
    }

    private void StartGeneration(ulong id, PbtNativeGroupBuffer data)
    {
        Generation generation = new(id, data);
        ulong index = id - _firstGeneration;
        Generation?[] generations = _generations;
        if (index < (ulong)generations.Length)
        {
            Volatile.Write(ref generations[index], generation);
        }
        else
        {
            Array.Resize(ref generations, generations.Length * 2);
            generations[index] = generation;
            Volatile.Write(ref _generations, generations);
        }

        Volatile.Write(ref _active, generation);
    }

    private void FreeGenerations()
    {
        Generation?[] generations = _generations;
        Volatile.Write(ref _generations, new Generation?[InitialGenerationSlots]);
        foreach (Generation? generation in generations) ((IDisposable?)generation?.Data)?.Dispose();
    }

    private void AddPin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pinCount++;
    }

    private void ReleasePin()
    {
        lock (_lock)
        {
            if (--_pinCount == 0 && _disposed) FreeGenerations();
        }
    }

    private sealed class Generation(ulong id, PbtNativeGroupBuffer data)
    {
        public readonly ulong Id = id;
        public readonly PbtNativeGroupBuffer Data = data;
        public long Used;
    }

    private sealed class WriteBatch(ulong basePointer, PbtNativeGroupBuffer data, int offset, int length) : IPbtGroupRecordWriteBatch
    {
        public ulong BasePointer => basePointer;

        public Span<byte> Bytes => data.AsSpan(offset, length);

        public void Dispose() { }
    }

    private sealed class BufferPin(PbtPrivateGroupRecordBuffer buffer, ulong writeBoundary) : IPbtGroupRecordBufferPin
    {
        private int _disposed;

        public IPbtGroupRecordBuffer Buffer => buffer;

        public ulong WriteBoundary => writeBoundary;

        public IPbtGroupRecordBufferPin Copy()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lock (buffer._lock) buffer.AddPin();
            return new BufferPin(buffer, writeBoundary);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) buffer.ReleasePin();
        }
    }
}
