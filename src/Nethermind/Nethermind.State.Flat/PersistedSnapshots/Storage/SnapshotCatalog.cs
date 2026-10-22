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
/// Rows of the earlier 48-byte <c>(To.BlockNumber, To.StateRoot, depth)</c> layout are re-keyed on
/// <see cref="Load"/>; see <see cref="MigrateLegacyRows"/>.
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
    public IEnumerable<CatalogEntry> Load()
    {
        MigrateLegacyRows();

        foreach (KeyValuePair<byte[], byte[]> kv in _db.GetAll(ordered: false))
        {
            // Entry keys are exactly KeySize; skip any other key (e.g. a legacy version word).
            if (kv.Key.Length != KeySize) continue;
            if (kv.Value.Length != EntrySize) continue;
            yield return ReadEntry(kv.Value);
        }
    }

    /// <summary>
    /// Re-keys every row of the legacy <c>(To, depth)</c> layout to <c>(To, tier)</c> in one atomic batch.
    /// </summary>
    /// <remarks>
    /// The legacy key let several rows share one bucket slot: a recompaction to a different depth replaced
    /// the bucket's snapshot, reclaiming its region, but left its row behind. The rows do not tell the live
    /// one from the stale ones, so a <c>(To, tier)</c> with more than one legacy row is dropped whole; its
    /// state is rebuilt by re-execution like any other missing persisted snapshot.
    /// </remarks>
    private void MigrateLegacyRows()
    {
        Dictionary<(StateId To, SnapshotTier Tier), List<KeyValuePair<byte[], byte[]>>> legacyRows = [];
        foreach (KeyValuePair<byte[], byte[]> kv in _db.GetAll(ordered: false))
        {
            if (kv.Key.Length != LegacyKeySize || kv.Value.Length != EntrySize) continue;
            CatalogEntry entry = ReadEntry(kv.Value);
            (StateId, SnapshotTier) slot = (entry.To, entry.Tier);
            if (!legacyRows.TryGetValue(slot, out List<KeyValuePair<byte[], byte[]>>? rows))
                legacyRows[slot] = rows = [];
            rows.Add(kv);
        }

        if (legacyRows.Count == 0) return;

        using IWriteBatch batch = _db.StartWriteBatch();
        Span<byte> key = stackalloc byte[KeySize];
        foreach (KeyValuePair<(StateId To, SnapshotTier Tier), List<KeyValuePair<byte[], byte[]>>> slot in legacyRows)
        {
            if (slot.Value.Count == 1)
            {
                WriteKey(key, slot.Key.To, slot.Key.Tier);
                batch.PutSpan(key, slot.Value[0].Value);
            }

            foreach (KeyValuePair<byte[], byte[]> row in slot.Value)
                batch.Remove(row.Key);
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
