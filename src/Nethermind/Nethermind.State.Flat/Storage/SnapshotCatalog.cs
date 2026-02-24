// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.PersistedSnapshots;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Persists snapshot metadata to a binary catalog file.
/// Supports add, remove, save, and load operations.
/// </summary>
public sealed class SnapshotCatalog
{
    /// <summary>
    /// A single catalog entry describing a persisted snapshot's identity and location.
    /// </summary>
    public sealed record CatalogEntry(
        int Id,
        StateId From,
        StateId To,
        PersistedSnapshotType Type,
        SnapshotLocation Location);

    // Binary layout per entry: Id(4) + From.Block(8) + From.Root(32) + To.Block(8) + To.Root(32) + Type(1) + ArenaId(4) + Offset(8) + Size(4) = 101
    internal const int EntrySize = 101;

    private readonly string _catalogPath;
    private readonly string _tempPath;
    private readonly List<CatalogEntry> _entries = [];
    private int _nextId = 1;

    public SnapshotCatalog(string catalogPath)
    {
        _catalogPath = catalogPath;
        _tempPath = catalogPath + ".tmp";
    }

    public IReadOnlyList<CatalogEntry> Entries => _entries;
    public int NextId() => _nextId++;

    public void Add(CatalogEntry entry) => _entries.Add(entry);

    public bool Remove(int snapshotId)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Id == snapshotId)
            {
                _entries.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    public CatalogEntry? Find(int snapshotId)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Id == snapshotId) return _entries[i];
        }
        return null;
    }

    /// <summary>
    /// Update the location of a catalog entry (used after arena compaction).
    /// </summary>
    public void UpdateLocation(int snapshotId, SnapshotLocation newLocation)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Id == snapshotId)
            {
                _entries[i] = _entries[i] with { Location = newLocation };
                return;
            }
        }
    }

    /// <summary>
    /// Save catalog to disk using atomic temp-file + rename.
    /// </summary>
    public void Save()
    {
        int totalSize = 8 + _entries.Count * EntrySize; // header(8) + entries
        byte[] buffer = new byte[totalSize];
        Span<byte> span = buffer;

        BinaryPrimitives.WriteInt32LittleEndian(span, _entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], _nextId);

        int offset = 8;
        foreach (CatalogEntry entry in _entries)
        {
            WriteEntry(span[offset..], entry);
            offset += EntrySize;
        }

        File.WriteAllBytes(_tempPath, buffer);
        File.Move(_tempPath, _catalogPath, overwrite: true);
    }

    /// <summary>
    /// Load catalog from disk.
    /// </summary>
    public void Load()
    {
        _entries.Clear();
        _nextId = 1;

        if (!File.Exists(_catalogPath)) return;

        byte[] buffer = File.ReadAllBytes(_catalogPath);
        if (buffer.Length < 8) return;

        ReadOnlySpan<byte> span = buffer;
        int count = BinaryPrimitives.ReadInt32LittleEndian(span);
        _nextId = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);

        int offset = 8;
        for (int i = 0; i < count && offset + EntrySize <= buffer.Length; i++)
        {
            _entries.Add(ReadEntry(span[offset..]));
            offset += EntrySize;
        }
    }

    private static void WriteEntry(Span<byte> span, CatalogEntry entry)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span, entry.Id);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], entry.From.BlockNumber);
        entry.From.StateRoot.BytesAsSpan.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt64LittleEndian(span[44..], entry.To.BlockNumber);
        entry.To.StateRoot.BytesAsSpan.CopyTo(span[52..]);
        span[84] = (byte)entry.Type;
        BinaryPrimitives.WriteInt32LittleEndian(span[85..], entry.Location.ArenaId);
        BinaryPrimitives.WriteInt64LittleEndian(span[89..], entry.Location.Offset);
        BinaryPrimitives.WriteInt32LittleEndian(span[97..], entry.Location.Size);
    }

    private static CatalogEntry ReadEntry(ReadOnlySpan<byte> span)
    {
        int id = BinaryPrimitives.ReadInt32LittleEndian(span);

        long fromBlock = BinaryPrimitives.ReadInt64LittleEndian(span[4..]);
        ValueHash256 fromRoot = new(span.Slice(12, 32));
        StateId from = new(fromBlock, fromRoot);

        long toBlock = BinaryPrimitives.ReadInt64LittleEndian(span[44..]);
        ValueHash256 toRoot = new(span.Slice(52, 32));
        StateId to = new(toBlock, toRoot);

        PersistedSnapshotType type = (PersistedSnapshotType)span[84];
        int arenaId = BinaryPrimitives.ReadInt32LittleEndian(span[85..]);
        long offset = BinaryPrimitives.ReadInt64LittleEndian(span[89..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(span[97..]);

        return new CatalogEntry(id, from, to, type, new SnapshotLocation(arenaId, offset, size));
    }
}
