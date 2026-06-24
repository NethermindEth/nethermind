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
/// CompactSized) so each survives independently across a restart. The reserved 4-byte
/// key stores the catalog-version word; entry keys are 48 bytes, so the lengths
/// cannot collide.
/// </summary>
public sealed class SnapshotCatalog(IDb db) : ISnapshotCatalog
{
    // Binary layout per entry: fromBlock(8) + fromRoot(32) + toBlock(8) + toRoot(32) +
    // arenaId(4) + offset(8) + size(8) + tier(1) = 101
    private const int EntrySize = 101;

    private const int KeySize = 48;

    // Catalog version: bumped when the on-disk binary layout changes incompatibly. Old
    // directories will fail to load with a clear "wipe and resync" message.
    // v2: persisted-snapshot metadata switched from the columnar format to the single-level
    // sorted table — the old metadata blobs are unreadable by the new reader.
    // v3: sorted table moved to a sparse (per-8-record) offset index, 1-byte key/value sizes, and
    // per-id ref-id records — incompatible with the v2 dense-offset layout.
    // v4: sorted-table keys are front-coded (per-block prefix compression) — incompatible record
    // layout vs v3.
    // v5: sorted table became two-level — 4 KB data blocks with an in-block restart table and a
    // tail separator-key index — incompatible with the v4 single-level sparse-offset layout.
    // v6: sorted table reuses one self-describing block format for both levels; data blocks are
    // 4 KiB-aligned and addressed by block number, and the index is a single block (separator →
    // block number) — incompatible with the v5 byte-offset tail index.
    // v7: sorted-table footer widened to i64 fields and the (unaligned) index block is located by a
    // stored byte offset instead of being recomputed from the block count — incompatible footer.
    // v8: index values are data-block byte offsets (u48), RocksDB-style delta-coded, instead of block
    // numbers — incompatible index encoding.
    // v9: sorted-table footer dropped the record-count and data-block-count fields (the enumerator now
    // walks the index block to locate data blocks) — incompatible footer.
    // v10: sorted-table footer dropped the restart-interval byte; restarts are now marked by cp == 0,
    // which also re-anchors the index block's delta-coded values — incompatible footer + index decode.
    private const int CurrentVersion = 10;

    private static readonly byte[] MetadataKey = new byte[4];

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
    /// Streams catalog entries lazily (unordered). The version check and first-write of the
    /// metadata word happen eagerly before the iterator is returned, not on enumeration.
    /// </summary>
    public IEnumerable<CatalogEntry> Load()
    {
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
        else
        {
            WriteMetadata();
        }

        return EnumerateEntries();
    }

    private IEnumerable<CatalogEntry> EnumerateEntries()
    {
        foreach (KeyValuePair<byte[], byte[]?> kv in _db.GetAll(ordered: false))
        {
            // Entry keys are exactly KeySize; the metadata key is 4 bytes.
            if (kv.Key.Length != KeySize) continue;
            if (kv.Value is null || kv.Value.Length != EntrySize) continue;
            yield return ReadEntry(kv.Value);
        }
    }

    private void WriteMetadata()
    {
        byte[] value = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(value, CurrentVersion);
        _db.Set(MetadataKey, value);
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
