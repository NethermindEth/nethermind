// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Collections;

/// <summary>Collects storage slots keyed by raw slot and replays them ordered by slot hash, the order of a storage trie's leaves.</summary>
/// <remarks>
/// The first <see cref="MaxInMemoryEntries"/> slots are buffered in memory; past that, all of them are spilled into a
/// <see cref="SortedSpool"/> under a fresh subdirectory of the given directory, so memory stays bounded however large the storage is.
/// Add every slot, call <see cref="CompleteAdding"/> once, then iterate with <see cref="MoveNext"/>.
/// </remarks>
internal sealed class HashSortedSlotPool(string directory, ILogManager logManager, CancellationToken cancellationToken) : IDisposable
{
    private const int SpoolBufferBytes = 64 * 1024 * 1024;
    private const int SlotLength = 32;

    internal int MaxInMemoryEntries { get; init; } = 1_000_000;

    private ArrayPoolList<Entry>? _entries = new(0);
    private int _index = -1;
    private Entry _current;
    private string? _spoolDirectory;
    private SortedSpool? _spool;
    private SortedSpool.Writer? _writer;
    private SortedSpool.Cursor? _cursor;

    // Fields rather than properties, so Value can hand out a span over this instance's copy instead of a temporary.
    private readonly struct Entry(ValueHash256 slotHash, ValueHash256 slot, ValueHash256 value)
    {
        public readonly ValueHash256 SlotHash = slotHash;
        public readonly ValueHash256 Slot = slot;
        public readonly ValueHash256 Value = value;
    }

    public ValueHash256 SlotHash => _current.SlotHash;
    public ValueHash256 Slot => _current.Slot;

    /// <summary>The slot value, left-padded with zeros to 32 bytes; valid until the next <see cref="MoveNext"/>.</summary>
    public ReadOnlySpan<byte> Value => _current.Value.Bytes;

    /// <param name="slot">The raw big-endian slot.</param>
    /// <param name="value">The slot value, at most 32 bytes.</param>
    public void Add(in ValueHash256 slot, ReadOnlySpan<byte> value)
    {
        ValueHash256 paddedValue = default;
        value.CopyTo(paddedValue.BytesAsSpan[(SlotLength - value.Length)..]);
        Entry entry = new(ValueKeccak.Compute(slot.Bytes), slot, paddedValue);

        if (_entries is not null)
        {
            if (_entries.Count < MaxInMemoryEntries)
            {
                _entries.Add(entry);
                return;
            }

            Spill(_entries);
            _entries = null;
        }

        Write(entry);
    }

    public void CompleteAdding()
    {
        if (_entries is not null)
        {
            _entries.AsSpan().Sort(static (left, right) => left.SlotHash.CompareTo(right.SlotHash));
            return;
        }

        _writer!.Dispose();
        _cursor = _spool!.Read();
    }

    public bool MoveNext()
    {
        if (_entries is not null)
        {
            if (++_index >= _entries.Count) return false;
            _current = _entries[_index];
            return true;
        }

        if (!_cursor!.MoveNext()) return false;
        ReadOnlySpan<byte> record = _cursor.Value;
        _current = new Entry(new ValueHash256(_cursor.Key), new ValueHash256(record[..SlotLength]), new ValueHash256(record[SlotLength..]));
        return true;
    }

    private void Spill(ArrayPoolList<Entry> entries)
    {
        _spoolDirectory = Directory.CreateDirectory(Path.Combine(directory, Guid.NewGuid().ToString("N"))).FullName;
        _spool = new SortedSpool(_spoolDirectory, SpoolBufferBytes, writerCount: 1, logManager, cancellationToken);
        _writer = _spool.CreateWriter();
        foreach (Entry entry in entries.AsSpan()) Write(entry);
        entries.Dispose();
    }

    private void Write(in Entry entry)
    {
        Span<byte> record = stackalloc byte[2 * SlotLength];
        entry.Slot.Bytes.CopyTo(record);
        entry.Value.Bytes.CopyTo(record[SlotLength..]);
        _writer!.Add(entry.SlotHash.Bytes, record);
    }

    public void Dispose()
    {
        _entries?.Dispose();
        _cursor?.Dispose();
        _writer?.Dispose();
        _spool?.Dispose();
        if (_spoolDirectory is not null) Directory.Delete(_spoolDirectory, recursive: true);
    }
}
