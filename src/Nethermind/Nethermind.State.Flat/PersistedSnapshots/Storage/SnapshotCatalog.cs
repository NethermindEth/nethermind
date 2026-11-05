// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Persists snapshot metadata in a key-value store, keyed by the 41-byte big-endian tuple
/// <c>(To.BlockNumber, To.StateRoot, tier)</c> — the identity the per-tier persisted buckets key their
/// snapshots by, so a bucket overwrite also overwrites the row and no row outlives its snapshot.
/// </summary>
/// <remarks>
/// Rows of the earlier 48-byte <c>(To.BlockNumber, To.StateRoot, depth)</c> layout are migrated on
/// <see cref="Load"/>.
/// </remarks>
public sealed class SnapshotCatalog([KeyFilter(DbNames.PersistedSnapshotCatalog)] IDb db) : ISnapshotCatalog
{
    // On-disk entry value, blitted to/from the store. Pack=1 keeps the fields at fixed contiguous byte
    // offsets (fromBlock 0, fromRoot 8, toBlock 40, toRoot 48, arenaId 80, offset 84, size 92, tier 100).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct EntryBytes(CatalogEntry entry)
    {
        // On-disk key/value layout stays Int64; the PreGenesis sentinel (ulong.MaxValue) round-trips
        // through long as -1, preserving the pre-ulong encoding.
        internal readonly long FromBlock = (long)entry.From.BlockNumber;
        internal readonly ValueHash256 FromRoot = entry.From.StateRoot;
        internal readonly long ToBlock = (long)entry.To.BlockNumber;
        internal readonly ValueHash256 ToRoot = entry.To.StateRoot;
        internal readonly int ArenaId = entry.Location.ArenaId;
        internal readonly long Offset = entry.Location.Offset;
        internal readonly long Size = entry.Location.Size;
        internal readonly byte Tier = (byte)entry.Tier;
    }

    private static readonly int EntrySize = Unsafe.SizeOf<EntryBytes>();

    private const int KeySize = 41;
    private const int LegacyKeySize = 48;

    private readonly IDb _db = db;

    public void Add(CatalogEntry entry)
    {
        Span<byte> key = stackalloc byte[KeySize];
        WriteKey(key, entry.To, entry.Tier);
        EntryBytes value = new(entry);
        _db.PutSpan(key, MemoryMarshal.AsBytes(new Span<EntryBytes>(ref value)));
    }

    public bool Remove(in StateId to, SnapshotTier tier)
    {
        Span<byte> key = stackalloc byte[KeySize];
        WriteKey(key, to, tier);
        if (!_db.KeyExists(key)) return false;
        _db.Remove(key);
        return true;
    }

    /// <summary>
    /// Streams catalog entries lazily (unordered). The catalog carries no version of its own; the on-disk
    /// format is identified by each snapshot's last byte and validated by the loader.
    /// </summary>
    /// <remarks>
    /// Rows of the legacy <c>(To, depth)</c> layout are migrated in the same pass, committed in one batch once
    /// the enumeration ends. That key let a recompaction to a different depth leave the replaced snapshot's row
    /// behind, pointing at its reclaimed region, and a lone legacy row may be such a leftover. Only
    /// <see cref="SnapshotTier.PersistedSmallCompacted"/> and <see cref="SnapshotTier.PersistedLargeCompacted"/>
    /// snapshots vary in depth, so their legacy rows are dropped (their state is served by the base and
    /// <c>CompactSize</c>-wide snapshots beneath them); the fixed-depth tiers are re-keyed.
    /// </remarks>
    public IEnumerable<CatalogEntry> Load()
    {
        List<KeyValuePair<byte[], byte[]>>? legacyRows = null;
        try
        {
            foreach (KeyValuePair<byte[], byte[]> kv in _db.GetAll(ordered: false))
            {
                // Entry keys are exactly KeySize, or LegacyKeySize until migrated; skip any other key (e.g. a legacy version word).
                if (kv.Value.Length != EntrySize) continue;
                if (kv.Key.Length == KeySize)
                {
                    yield return ReadEntry(kv.Value);
                }
                else if (kv.Key.Length == LegacyKeySize)
                {
                    (legacyRows ??= []).Add(kv);
                    CatalogEntry entry = ReadEntry(kv.Value);
                    if (IsMigrated(entry.Tier)) yield return entry;
                }
            }
        }
        finally
        {
            // Written once the scan is done, so the enumeration cannot meet a re-keyed row.
            if (legacyRows is not null) MigrateLegacyRows(legacyRows);
        }
    }

    private static bool IsMigrated(SnapshotTier tier) =>
        tier is not (SnapshotTier.PersistedSmallCompacted or SnapshotTier.PersistedLargeCompacted);

    private void MigrateLegacyRows(List<KeyValuePair<byte[], byte[]>> legacyRows)
    {
        using IWriteBatch batch = _db.StartWriteBatch();
        Span<byte> key = stackalloc byte[KeySize];
        foreach (KeyValuePair<byte[], byte[]> row in legacyRows)
        {
            batch.Remove(row.Key);
            CatalogEntry entry = ReadEntry(row.Value);
            if (!IsMigrated(entry.Tier)) continue;
            WriteKey(key, entry.To, entry.Tier);
            batch.PutSpan(key, row.Value);
        }
    }

    private static void WriteKey(Span<byte> span, in StateId to, SnapshotTier tier)
    {
        BinaryPrimitives.WriteInt64BigEndian(span, (long)to.BlockNumber);
        to.StateRoot.BytesAsSpan.CopyTo(span[8..]);
        span[40] = (byte)tier;
    }

    private static CatalogEntry ReadEntry(ReadOnlySpan<byte> span)
    {
        EntryBytes e = MemoryMarshal.Read<EntryBytes>(span);
        SnapshotTier tier = (SnapshotTier)e.Tier;
        if (!tier.IsPersisted())
            throw new InvalidOperationException(
                $"Persisted snapshot catalog entry has non-persisted tier byte {e.Tier} (only Persisted* tiers are ever stored). " +
                "The persistedSnapshot/ directory has an incompatible or corrupted layout — wipe and resync.");

        return new CatalogEntry(
            new StateId((ulong)e.FromBlock, e.FromRoot),
            new StateId((ulong)e.ToBlock, e.ToRoot),
            new SnapshotLocation(e.ArenaId, e.Offset, e.Size),
            tier);
    }
}
