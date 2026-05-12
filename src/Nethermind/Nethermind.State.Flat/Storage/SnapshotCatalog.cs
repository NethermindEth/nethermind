// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Persists snapshot metadata in a key-value store (RocksDB column or MemDb).
/// Each entry is keyed by its 40-byte <see cref="StateId"/> <c>To</c>
/// (8-byte big-endian block number followed by the 32-byte state root), matching
/// the in-memory dictionary keys used by <c>PersistedSnapshotRepository</c>. The
/// reserved 4-byte key stores the catalog-version word; entry keys are 40 bytes,
/// so the lengths cannot collide.
/// </summary>
public sealed class SnapshotCatalog(IDb db)
{
    /// <summary>
    /// A single catalog entry describing a persisted snapshot's identity and location.
    /// </summary>
    public sealed record CatalogEntry(
        StateId From,
        StateId To,
        SnapshotLocation Location);

    // Binary layout per entry: fromBlock(8) + fromRoot(32) + toBlock(8) + toRoot(32) + arenaId(4) + offset(8) + size(8) = 100
    internal const int EntrySize = 100;

    // 8-byte block number + 32-byte state root, matching the StateId layout.
    internal const int KeySize = 40;

    // Catalog version: bumped when the on-disk binary layout changes incompatibly. Old
    // directories will fail to load with a clear "wipe and resync" message. v2 was the
    // BlobArena-backed layout (no PersistedSnapshotType byte, ref_ids are blob arena ids).
    // v3: blob arena ids are now per-file (was per-slice); NodeRef.RlpDataOffset is now
    // file-absolute (was slice-relative); entries are keyed by StateId.To and the
    // per-entry Id field is gone.
    internal const int CurrentVersion = 3;

    // Length-4 sentinel key holding the version word. Entry keys are 40 bytes, so the
    // length disambiguation is unambiguous when iterating GetAll().
    private static readonly byte[] MetadataKey = new byte[4];

    private readonly IDb _db = db;
    private readonly List<CatalogEntry> _entries = [];

    public IReadOnlyList<CatalogEntry> Entries => _entries;

    public void Add(CatalogEntry entry)
    {
        _entries.Add(entry);
        Span<byte> key = stackalloc byte[KeySize];
        WriteKey(key, entry.To);
        byte[] value = new byte[EntrySize];
        WriteEntry(value, entry);
        _db.Set(key, value);
    }

    public bool Remove(in StateId to)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].To == to)
            {
                _entries.RemoveAt(i);
                Span<byte> key = stackalloc byte[KeySize];
                WriteKey(key, to);
                _db.Remove(key);
                return true;
            }
        }
        return false;
    }

    public CatalogEntry? Find(in StateId to)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].To == to) return _entries[i];
        }
        return null;
    }

    /// <summary>
    /// Update the location of a catalog entry (used after arena compaction).
    /// </summary>
    public void UpdateLocation(in StateId to, SnapshotLocation newLocation)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].To == to)
            {
                CatalogEntry updated = _entries[i] with { Location = newLocation };
                _entries[i] = updated;
                Span<byte> key = stackalloc byte[KeySize];
                WriteKey(key, to);
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

        byte[]? meta = _db.Get(MetadataKey);
        if (meta is not null)
        {
            if (meta.Length != 4)
                throw new InvalidOperationException(
                    $"Persisted snapshot catalog metadata has unexpected length {meta.Length} (expected 4). " +
                    "The persisted_snapshot/ directory has an incompatible layout — wipe and resync.");

            int version = BinaryPrimitives.ReadInt32LittleEndian(meta);
            if (version != CurrentVersion)
                throw new InvalidOperationException(
                    $"Persisted snapshot catalog version mismatch: on-disk v{version}, runtime expects v{CurrentVersion}. " +
                    "The persisted_snapshot/ directory has an incompatible layout — wipe and resync.");
        }

        foreach (KeyValuePair<byte[], byte[]?> kv in _db.GetAll(ordered: false))
        {
            // Entry keys are exactly KeySize; the metadata key is 4 bytes.
            if (kv.Key.Length != KeySize) continue;
            if (kv.Value is null || kv.Value.Length != EntrySize) continue;
            _entries.Add(ReadEntry(kv.Value));
        }

        // Stable order by To.BlockNumber so callers that depend on insertion order keep working.
        _entries.Sort(static (a, b) => a.To.BlockNumber.CompareTo(b.To.BlockNumber));

        // Persist the version word if the catalog has never been written before.
        if (meta is null)
            WriteMetadata();
    }

    private void WriteMetadata()
    {
        byte[] value = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(value, CurrentVersion);
        _db.Set(MetadataKey, value);
    }

    private static void WriteKey(Span<byte> span, in StateId to)
    {
        BinaryPrimitives.WriteInt64BigEndian(span, to.BlockNumber);
        to.StateRoot.BytesAsSpan.CopyTo(span[8..]);
    }

    private static void WriteEntry(Span<byte> span, CatalogEntry entry)
    {
        BinaryPrimitives.WriteInt64LittleEndian(span, entry.From.BlockNumber);
        entry.From.StateRoot.BytesAsSpan.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt64LittleEndian(span[40..], entry.To.BlockNumber);
        entry.To.StateRoot.BytesAsSpan.CopyTo(span[48..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[80..], entry.Location.ArenaId);
        BinaryPrimitives.WriteInt64LittleEndian(span[84..], entry.Location.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(span[92..], entry.Location.Size);
    }

    private static CatalogEntry ReadEntry(ReadOnlySpan<byte> span)
    {
        long fromBlock = BinaryPrimitives.ReadInt64LittleEndian(span);
        ValueHash256 fromRoot = new(span.Slice(8, 32));
        StateId from = new(fromBlock, fromRoot);

        long toBlock = BinaryPrimitives.ReadInt64LittleEndian(span[40..]);
        ValueHash256 toRoot = new(span.Slice(48, 32));
        StateId to = new(toBlock, toRoot);

        int arenaId = BinaryPrimitives.ReadInt32LittleEndian(span[80..]);
        long offset = BinaryPrimitives.ReadInt64LittleEndian(span[84..]);
        long size = BinaryPrimitives.ReadInt64LittleEndian(span[92..]);

        return new CatalogEntry(from, to, new SnapshotLocation(arenaId, offset, size));
    }
}
