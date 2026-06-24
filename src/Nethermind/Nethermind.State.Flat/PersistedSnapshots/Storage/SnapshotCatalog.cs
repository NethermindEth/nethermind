// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Persists snapshot metadata in a key-value store (RocksDB column or MemDb).
/// Each entry is keyed by its 48-byte tuple <c>(To.BlockNumber, To.StateRoot, depth)</c>
/// — 8-byte big-endian block number, 32-byte state root, 8-byte big-endian depth
/// (<c>To.BlockNumber - From.BlockNumber</c>). The depth disambiguates entries that
/// share the same <c>To</c> across the three runtime buckets (base, compacted,
/// CompactSized) so each survives independently across a restart. The catalog stores no format
/// version of its own — the on-disk format is identified by each snapshot's last byte (its
/// sorted-table format version), which the loader validates.
/// </summary>
public sealed class SnapshotCatalog(IDb db) : ISnapshotCatalog
{
    // Binary layout per entry: fromBlock(8) + fromRoot(32) + toBlock(8) + toRoot(32) +
    // arenaId(4) + offset(8) + size(8) + tier(1) = 101
    private const int EntrySize = 101;

    private const int KeySize = 48;

    private readonly IDb _db = db;

    public void Add(CatalogEntry entry)
    {
        Span<byte> key = stackalloc byte[KeySize];
        WriteKey(key, entry.To, Depth(entry));
        byte[] value = new byte[EntrySize];
        WriteEntry(value, entry);
        _db.Set(key, value);
    }

    public bool Remove(in StateId to, long depth)
    {
        Span<byte> key = stackalloc byte[KeySize];
        WriteKey(key, to, depth);
        if (!_db.KeyExists(key)) return false;
        _db.Remove(key);
        return true;
    }

    private static long Depth(CatalogEntry entry) => entry.To.BlockNumber - entry.From.BlockNumber;

    /// <summary>
    /// Streams catalog entries lazily (unordered). The catalog carries no version of its own; the on-disk
    /// format is identified by each snapshot's last byte and validated by the loader.
    /// </summary>
    public IEnumerable<CatalogEntry> Load()
    {
        foreach (KeyValuePair<byte[], byte[]?> kv in _db.GetAll(ordered: false))
        {
            // Entry keys are exactly KeySize; skip any other key (e.g. a legacy version word).
            if (kv.Key.Length != KeySize) continue;
            if (kv.Value is null || kv.Value.Length != EntrySize) continue;
            yield return ReadEntry(kv.Value);
        }
    }

    private static void WriteKey(Span<byte> span, in StateId to, long depth)
    {
        BinaryPrimitives.WriteInt64BigEndian(span, to.BlockNumber);
        to.StateRoot.BytesAsSpan.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt64BigEndian(span[40..], depth);
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
        span[100] = (byte)entry.Tier;
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
        SnapshotTier tier = (SnapshotTier)span[100];
        if (!tier.IsPersisted())
            throw new InvalidOperationException(
                $"Persisted snapshot catalog entry has non-persisted tier byte {span[100]} (only Persisted* tiers are ever stored). " +
                "The persisted_snapshot/ directory has an incompatible or corrupted layout — wipe and resync.");

        return new CatalogEntry(from, to, new SnapshotLocation(arenaId, offset, size), tier);
    }
}
