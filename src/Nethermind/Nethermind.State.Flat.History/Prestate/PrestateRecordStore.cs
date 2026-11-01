// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;

namespace Nethermind.State.Flat.History.Prestate;

/// <summary>The prestateRecords database: one record per parent state, keyed by the parent number and state root,
/// and the prune floor.</summary>
/// <remarks>The floor key starts with eight 0xFF bytes, so it sorts after every record key and no record range
/// deletion can reach it.</remarks>
public sealed class PrestateRecordStore
{
    private static ReadOnlySpan<byte> FloorKey => [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, (byte)'f', (byte)'l', (byte)'o', (byte)'o', (byte)'r'];

    private readonly IDb _db;
    private ulong _floor;
    private long _generation;

    public PrestateRecordStore(IDb db)
    {
        _db = db;
        byte[]? floor = db.Get(FloorKey);
        _floor = floor is { Length: sizeof(ulong) } ? BinaryPrimitives.ReadUInt64BigEndian(floor) : 0;
    }

    /// <summary>Records of parent states below this block are pruned or about to be; none is read.</summary>
    public ulong Floor => Volatile.Read(ref _floor);

    /// <summary>Moves on every write, so a cached "no record" taken before a write is not trusted after it.</summary>
    public long Generation => Volatile.Read(ref _generation);

    [SkipLocalsInit]
    public void Write(in StateId parent, ReadOnlySpan<byte> record)
    {
        Span<byte> key = stackalloc byte[PrestateRecordFormat.KeyLength];
        PrestateRecordFormat.WriteKey(parent, key);
        _db.PutSpan(key, record, WriteFlags.LowPriority);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Opens the record of <paramref name="parent"/>. <paramref name="found"/> tells an absent record from one
    /// that is present but malformed or of another state.</summary>
    [SkipLocalsInit]
    public PrestateRecord? Read(in StateId parent, out bool found)
    {
        Span<byte> key = stackalloc byte[PrestateRecordFormat.KeyLength];
        PrestateRecordFormat.WriteKey(parent, key);
        Span<byte> value = _db.GetSpan(key, ReadFlags.HintCacheMiss);
        try
        {
            found = !value.IsNull();
            return found ? PrestateRecord.TryOpen(value, parent) : null;
        }
        finally
        {
            _db.DangerousReleaseMemory(value);
        }
    }

    /// <summary>Drops every record whose parent is below <paramref name="floor"/>. The floor is written first, so a
    /// crash between the two leaves records no reader accepts, never a floor that claims deleted records exist.</summary>
    [SkipLocalsInit]
    public void PruneBelow(ulong floor, bool reclaim)
    {
        if (floor <= Floor) return;

        Span<byte> value = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, floor);
        _db.PutSpan(FloorKey, value);
        Volatile.Write(ref _floor, floor);
        _db.DeleteBlockNumberRange(0, floor);
        if (reclaim) _db.ReclaimBlockNumberRange(0, floor);
    }
}
