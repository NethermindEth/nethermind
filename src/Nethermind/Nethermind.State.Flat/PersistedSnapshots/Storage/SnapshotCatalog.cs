// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Persists snapshot metadata in a key-value store, keyed by the 48-byte big-endian tuple
/// <c>(To.BlockNumber, To.StateRoot, depth)</c> where <c>depth = To.BlockNumber - From.BlockNumber</c>
/// distinguishes entries that share a <c>To</c> across the base/compacted/CompactSized buckets.
/// </summary>
public sealed class SnapshotCatalog(IDb db) : ISnapshotCatalog
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

    private const int KeySize = 48;

    private readonly IDb _db = db;

    public void Add(CatalogEntry entry)
    {
        Span<byte> key = stackalloc byte[KeySize];
        WriteKey(key, entry.To, Depth(entry));
        EntryBytes value = new(entry);
        _db.Set(key, MemoryMarshal.AsBytes(new Span<EntryBytes>(ref value)).ToArray());
    }

    public bool Remove(in StateId to, long depth)
    {
        Span<byte> key = stackalloc byte[KeySize];
        WriteKey(key, to, depth);
        if (!_db.KeyExists(key)) return false;
        _db.Remove(key);
        return true;
    }

    private static long Depth(CatalogEntry entry) => (long)(entry.To.BlockNumber - entry.From.BlockNumber);

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
        BinaryPrimitives.WriteInt64BigEndian(span, (long)to.BlockNumber);
        to.StateRoot.BytesAsSpan.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt64BigEndian(span[40..], depth);
    }

    private static CatalogEntry ReadEntry(ReadOnlySpan<byte> span)
    {
        EntryBytes e = MemoryMarshal.Read<EntryBytes>(span);
        SnapshotTier tier = (SnapshotTier)e.Tier;
        if (!tier.IsPersisted())
            throw new InvalidOperationException(
                $"Persisted snapshot catalog entry has non-persisted tier byte {e.Tier} (only Persisted* tiers are ever stored). " +
                "The persisted_snapshot/ directory has an incompatible or corrupted layout — wipe and resync.");

        return new CatalogEntry(
            new StateId((ulong)e.FromBlock, e.FromRoot),
            new StateId((ulong)e.ToBlock, e.ToRoot),
            new SnapshotLocation(e.ArenaId, e.Offset, e.Size),
            tier);
    }
}
