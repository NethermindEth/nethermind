// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Persists snapshot metadata in a key-value store (RocksDB column or MemDb).
/// Each entry is stored under a 4-byte big-endian id key. The reserved key
/// <c>0x00000000</c> stores the next-id metadata word so an id is durable as
/// soon as <see cref="Add"/> commits — no separate flush needed.
/// </summary>
public sealed class SnapshotCatalog(IDb db)
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

    // Reserved id 0 holds (nextId:int32). Entry ids start at 1.
    private static readonly byte[] MetadataKey = new byte[4];

    private readonly IDb _db = db;
    private readonly List<CatalogEntry> _entries = [];
    private int _nextId = 1;

    public IReadOnlyList<CatalogEntry> Entries => _entries;

    public int NextId()
    {
        int id = _nextId++;
        WriteMetadata();
        return id;
    }

    public void Add(CatalogEntry entry)
    {
        _entries.Add(entry);
        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, entry.Id);
        byte[] value = new byte[EntrySize];
        WriteEntry(value, entry);
        _db.Set(key, value);
        if (entry.Id >= _nextId)
        {
            _nextId = entry.Id + 1;
            WriteMetadata();
        }
    }

    public bool Remove(int snapshotId)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Id == snapshotId)
            {
                _entries.RemoveAt(i);
                Span<byte> key = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(key, snapshotId);
                _db.Remove(key);
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
                CatalogEntry updated = _entries[i] with { Location = newLocation };
                _entries[i] = updated;
                Span<byte> key = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(key, snapshotId);
                byte[] value = new byte[EntrySize];
                WriteEntry(value, updated);
                _db.Set(key, value);
                return;
            }
        }
    }

    /// <summary>
    /// Each mutating operation persists immediately, so Save is a no-op.
    /// Kept for source-compat with the previous file-backed catalog.
    /// </summary>
    public void Save() { }

    /// <summary>
    /// Load all entries from the underlying DB into the in-memory list.
    /// </summary>
    public void Load()
    {
        _entries.Clear();
        _nextId = 1;

        byte[]? meta = _db.Get(MetadataKey);
        if (meta is { Length: 4 })
            _nextId = BinaryPrimitives.ReadInt32LittleEndian(meta);

        foreach (KeyValuePair<byte[], byte[]?> kv in _db.GetAll(ordered: false))
        {
            // Skip metadata key (id 0)
            if (kv.Key.Length == 4 && BinaryPrimitives.ReadInt32BigEndian(kv.Key) == 0) continue;
            if (kv.Value is null || kv.Value.Length != EntrySize) continue;
            _entries.Add(ReadEntry(kv.Value));
        }

        // Stable order by id so callers that depend on insertion order keep working.
        _entries.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        // If metadata was missing, reconstruct nextId from max(entry.Id) + 1.
        if (meta is null && _entries.Count > 0)
            _nextId = _entries[^1].Id + 1;
    }

    private void WriteMetadata()
    {
        byte[] value = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(value, _nextId);
        _db.Set(MetadataKey, value);
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
