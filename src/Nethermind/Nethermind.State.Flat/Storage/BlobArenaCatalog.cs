// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Db;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Persists the set of live blob arena reservations across restarts. Mirrors
/// <see cref="SnapshotCatalog"/>'s shape but for blob arenas, since snapshots
/// link to blob arenas rather than own them — a blob arena reservation can
/// outlive the snapshot that wrote it (still referenced by downstream
/// compacted snapshots) and must be findable on restart independently of any
/// individual snapshot's catalog entry.
///
/// <para>
/// One catalog instance per pool tier: the small tier has its own DB column
/// (<c>FlatDbColumns.SmallBlobArenaCatalog</c>), the large tier likewise.
/// Each instance only ever stores entries for its own pool, so the pool byte
/// is not part of the on-disk layout.
/// </para>
///
/// <para>
/// Keying: 4-byte big-endian <c>blobArenaId</c>. Reserved id 0 holds metadata
/// (<c>nextBlobArenaId:int32 LE + version:int32 LE</c>) so the id counter is
/// durable. Ids are unique within a catalog (i.e. within a tier), not across
/// tiers; the owning <see cref="BlobArenaManager"/> resolves an id through
/// its own catalog only.
/// </para>
///
/// <para>
/// Lifecycle: an entry is added by <see cref="BlobArenaManager"/> on
/// reservation creation, and removed when the last lease on the reservation
/// drops. The file holding the reservation is deleted by the underlying
/// <see cref="ArenaManager.MarkDead"/> path; catalog removal happens before
/// the deletion so a crash between the two leaves a dangling on-disk arena
/// file with no catalog entry — recoverable by scanning the directory on
/// next startup. The reverse order would leave a phantom catalog entry
/// pointing at a deleted file.
/// </para>
/// </summary>
public sealed class BlobArenaCatalog(IDb db) : IDisposable
{
    /// <summary>No-op; the underlying <see cref="IDb"/> is owned externally.
    /// Implemented so test code can wrap instances in <c>using</c> alongside
    /// the arena managers without ceremony.</summary>
    public void Dispose() { }

    /// <summary>
    /// One blob arena reservation, located on disk.
    /// <c>InternalArenaId</c> is the file id within the pool's
    /// <see cref="ArenaManager"/>; <c>(Offset, Size)</c> is its slice.
    /// </summary>
    public sealed record Entry(
        int BlobArenaId,
        SnapshotLocation Location);

    // Binary layout per entry: blobArenaId(4) + arenaId(4) + offset(8) + size(8) = 24
    internal const int EntrySize = 24;

    // Catalog version: bump when the on-disk binary layout changes incompatibly.
    // v2: dropped the Pool byte (each catalog now serves a single tier).
    internal const int CurrentVersion = 2;

    // Reserved id 0 holds (nextBlobArenaId:int32 LE, version:int32 LE).
    private static readonly byte[] MetadataKey = new byte[4];

    private readonly IDb _db = db;
    private readonly List<Entry> _entries = [];
    private int _nextBlobArenaId = 1;

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>
    /// Reserve and return the next globally-unique blob arena id. The counter
    /// is durable when <see cref="Add"/> persists the entry; if a writer is
    /// cancelled (no <c>Add</c>) the id is harmlessly skipped on next restart.
    /// </summary>
    public int NextId() => _nextBlobArenaId++;

    public void Add(Entry entry)
    {
        _entries.Add(entry);
        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, entry.BlobArenaId);
        byte[] value = new byte[EntrySize];
        WriteEntry(value, entry);
        _db.Set(key, value);
        if (entry.BlobArenaId >= _nextBlobArenaId)
        {
            _nextBlobArenaId = entry.BlobArenaId + 1;
            WriteMetadata();
        }
    }

    public bool Remove(int blobArenaId)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].BlobArenaId == blobArenaId)
            {
                _entries.RemoveAt(i);
                Span<byte> key = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(key, blobArenaId);
                _db.Remove(key);
                return true;
            }
        }
        return false;
    }

    public void Load()
    {
        _entries.Clear();
        _nextBlobArenaId = 1;

        byte[]? meta = _db.Get(MetadataKey);
        if (meta is { Length: >= 4 })
            _nextBlobArenaId = BinaryPrimitives.ReadInt32LittleEndian(meta);
        if (meta is { Length: >= 8 })
        {
            int version = BinaryPrimitives.ReadInt32LittleEndian(meta.AsSpan(4));
            if (version != CurrentVersion)
                throw new InvalidOperationException(
                    $"Blob arena catalog version mismatch: on-disk v{version}, runtime expects v{CurrentVersion}. " +
                    "The persisted_snapshot/ directory has an incompatible layout — wipe and resync.");
        }
        else if (meta is { Length: 4 })
        {
            throw new InvalidOperationException(
                $"Blob arena catalog is pre-v{CurrentVersion} (no version word). " +
                "The persisted_snapshot/ directory has an incompatible layout — wipe and resync.");
        }

        foreach (KeyValuePair<byte[], byte[]?> kv in _db.GetAll(ordered: false))
        {
            if (kv.Key.Length == 4 && BinaryPrimitives.ReadInt32BigEndian(kv.Key) == 0) continue;
            if (kv.Value is null || kv.Value.Length != EntrySize) continue;
            _entries.Add(ReadEntry(kv.Value));
        }

        _entries.Sort(static (a, b) => a.BlobArenaId.CompareTo(b.BlobArenaId));

        if (meta is null && _entries.Count > 0)
            _nextBlobArenaId = _entries[^1].BlobArenaId + 1;
    }

    private void WriteMetadata()
    {
        byte[] value = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(value, _nextBlobArenaId);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4), CurrentVersion);
        _db.Set(MetadataKey, value);
    }

    private static void WriteEntry(Span<byte> span, Entry entry)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span, entry.BlobArenaId);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], entry.Location.ArenaId);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], entry.Location.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], entry.Location.Size);
    }

    private static Entry ReadEntry(ReadOnlySpan<byte> span)
    {
        int id = BinaryPrimitives.ReadInt32LittleEndian(span);
        int arenaId = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        long offset = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
        long size = BinaryPrimitives.ReadInt64LittleEndian(span[16..]);
        return new Entry(id, new SnapshotLocation(arenaId, offset, size));
    }
}
