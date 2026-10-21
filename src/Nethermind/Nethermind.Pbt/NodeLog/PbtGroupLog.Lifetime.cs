// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading;

namespace Nethermind.Pbt.NodeLog;

/// <remarks>
/// Generation ids are contiguous: compacted generations come first, then those still in place. A generation outside the
/// retention window and older than every <see cref="GenerationPin"/> is first marked as dropping: lookups then miss it and
/// later <see cref="PinAll"/> calls skip it, so it waits only for the pins taken before the mark, however much later readers
/// overlap. It is compacted, evicted or freed once no <see cref="PinAll"/> covers it. Only the oldest consecutive compacted and
/// in-place generations are marked, so a <see cref="PinAll"/> pins two contiguous ranges.
/// </remarks>
public sealed partial class PbtGroupLog
{
    private uint _oldestCompressed = 1;
    private uint _oldestInPlace = 1;
    private int _pinCount;

    /// <summary>Keeps the active generation and every later generation readable until the pin is disposed.</summary>
    /// <remarks>The pin's <see cref="GenerationPin.WriteBoundary"/> is the allocation frontier at this call.</remarks>
    public GenerationPin Pin()
    {
        lock (_lock)
        {
            Generation? active = _active;
            uint generation = active?.Id ?? checked(_lastGeneration + 1);
            return PinGeneration(generation, new PbtGroupReference(generation, active is null ? 0 : Volatile.Read(ref active.Used)));
        }
    }

    IPbtGroupLogPin IPbtGroupLog.Pin() => Pin();

    /// <inheritdoc/>
    public IDisposable PinAll()
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            uint compressedFrom = PinFromFirstUnmarked(_oldestCompressed, _oldestInPlace);
            uint inPlaceFrom = PinFromFirstUnmarked(_oldestInPlace, _lastGeneration + 1);
            _pinCount++;
            return new AllGenerationsPin(this, compressedFrom, _oldestInPlace, inPlaceFrom, _lastGeneration + 1);
        }
    }

    private GenerationPin PinGeneration(uint generation, PbtGroupReference writeBoundary)
    {
        lock (_lock)
        {
            ThrowIfUnavailable();
            if (generation < _oldestInPlace || _generations.TryGetValue(generation, out Generation? pinned) && pinned.Dropping)
                throw new ObjectDisposedException(nameof(GenerationPin));
            _pins.TryGetValue(generation, out int count);
            _pins[generation] = count + 1;
            _pinCount++;
            return new GenerationPin(this, generation, writeBoundary);
        }
    }

    /// <summary>Pins the generations of [<paramref name="from"/>, <paramref name="to"/>) after its marked oldest ones.</summary>
    /// <returns>The first generation pinned.</returns>
    private uint PinFromFirstUnmarked(uint from, uint to)
    {
        while (from < to && _generations[from].Dropping) from++;
        for (uint id = from; id < to; id++) _generations[id].PinCount++;
        return from;
    }

    private void RemovePin(uint generation)
    {
        lock (_lock)
        {
            int count = _pins[generation];
            if (count == 1) _pins.Remove(generation);
            else _pins[generation] = count - 1;
            ReleasePin();
        }
    }

    private void RemovePins(uint compressedFrom, uint compressedTo, uint inPlaceFrom, uint inPlaceTo)
    {
        lock (_lock)
        {
            Unpin(compressedFrom, compressedTo);
            Unpin(inPlaceFrom, inPlaceTo);
            ReleasePin();
        }
    }

    private void Unpin(uint from, uint to)
    {
        for (uint id = from; id < to; id++)
        {
            Debug.Assert(_generations[id].PinCount > 0, "Only a pinned generation can be released.");
            _generations[id].PinCount--;
        }
    }

    private void ReleasePin()
    {
        _pinCount--;
        Trim(keepActive: false);
    }

    /// <summary>Stops new pins and writes, releasing storage once outstanding pins and compaction finish.</summary>
    /// <remarks>Does not wait for outstanding pins, which may be released by the disposing thread itself. Later calls do nothing.</remarks>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            Volatile.Write(ref _active, null);
            TryCompleteDispose();
        }
    }

    private uint OldestPinnedGeneration => _pins.Count == 0 ? uint.MaxValue : _pins.Keys[0];

    private bool CompactsDropped => _compressedMemoryBudget > 0 && !_disposed && _compactionFailure is null;

    private bool KeepOpportunistically(uint generation) => _lastGeneration - generation < (uint)_retainedGenerations;

    private bool IsCompactable => _oldestInPlace <= _lastGeneration && _generations[_oldestInPlace] is { Dropping: true, PinCount: 0 };

    /// <param name="keepActive">Whether to keep the active generation, which a writer that just rotated is about to reserve in.</param>
    private void Trim(bool keepActive)
    {
        if (_disposed)
        {
            TryCompleteDispose();
            return;
        }

        uint oldestPinned = OldestPinnedGeneration;
        for (uint id = _oldestInPlace; id <= _lastGeneration && id < oldestPinned && !KeepOpportunistically(id); id++)
        {
            Generation generation = _generations[id];
            if (keepActive && ReferenceEquals(_active, generation)) break;
            Volatile.Write(ref generation.Dropping, true);
            if (ReferenceEquals(_active, generation)) Volatile.Write(ref _active, null);
        }

        if (CompactsDropped) ScheduleCompaction();
        else if (!_compacting)
            while (IsCompactable) Release(_generations[_oldestInPlace], null);
        EvictCompressed();
    }

    /// <summary>Frees the in-place storage of the oldest in-place generation, which then resolves through what compaction kept.</summary>
    private void Release(Generation generation, CompressedGeneration? compressed)
    {
        Debug.Assert(generation.Id == _oldestInPlace && generation.PinCount == 0, "Only the oldest unpinned in-place generation is released.");
        if (compressed is not null)
        {
            _compressedBytes += compressed.Memory;
            Volatile.Write(ref generation.Compressed, compressed);
        }

        Interlocked.Add(ref _residentBytes, -generation.Buffer!.Capacity);
        ((IDisposable)generation.Buffer).Dispose();
        Volatile.Write(ref generation.Buffer, null);
        generation.Bloom?.Dispose();
        generation.Bloom = null;
        Volatile.Write(ref generation.Dropping, false);
        _oldestInPlace++;
    }

    private void TryCompleteDispose()
    {
        if (_pinCount != 0 || _compacting) return;

        foreach ((_, Generation generation) in _generations)
        {
            if (generation.Buffer is { } buffer) ((IDisposable)buffer).Dispose();
            generation.Bloom?.Dispose();
            generation.Compressed?.Dispose();
        }
        _generations.Clear();
        Interlocked.Exchange(ref _residentBytes, 0);
        _compressedBytes = 0;
        _oldestCompressed = _oldestInPlace = _lastGeneration + 1;
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _compactionFailure) is { } failure) throw new InvalidOperationException("PBT group log compaction failed.", failure);
    }

    /// <summary>Keeps one generation and every later generation available to snapshot readers.</summary>
    public sealed class GenerationPin : IPbtGroupLogPin
    {
        private readonly PbtGroupLog _log;
        private int _disposed;

        internal GenerationPin(PbtGroupLog log, uint generation, PbtGroupReference writeBoundary)
        {
            _log = log;
            Generation = generation;
            WriteBoundary = writeBoundary;
        }

        /// <summary>The first generation kept by this pin.</summary>
        public uint Generation { get; }

        /// <inheritdoc/>
        public IPbtGroupLog Log => _log;

        /// <inheritdoc/>
        public PbtGroupReference WriteBoundary { get; }

        /// <summary>Keeps the same generation range independently of this pin.</summary>
        /// <exception cref="ObjectDisposedException">This pin or the log is disposed.</exception>
        public GenerationPin Copy()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _log.PinGeneration(Generation, WriteBoundary);
        }

        IPbtGroupLogPin IPbtGroupLogPin.Copy() => Copy();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _log.RemovePin(Generation);
        }
    }

    private sealed class AllGenerationsPin(PbtGroupLog log, uint compressedFrom, uint compressedTo, uint inPlaceFrom, uint inPlaceTo) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) log.RemovePins(compressedFrom, compressedTo, inPlaceFrom, inPlaceTo);
        }
    }
}
