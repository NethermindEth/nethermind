// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Storage-clear events (<c>[account key | block BE] -> empty</c>), one per self-destruct that had persisted
/// storage. The live column expresses those as a range-delete with no per-slot tombstones, so an as-of read needs
/// this to tell that a slot written before the destruct is dead.
/// </summary>
internal sealed class StorageClearStore
{
    private const int BlockBytes = sizeof(ulong);

    private readonly ISortedKeyValueStore _clears;

    public StorageClearStore(IDb clears)
    {
        ArgumentNullException.ThrowIfNull(clears);
        if (clears is not ISortedKeyValueStore sortedClears)
            throw new ArgumentException($"Storage clears column must be a {nameof(ISortedKeyValueStore)}.", nameof(clears));

        _clears = sortedClears;
    }

    // Distinguishes an over-cap clear from a normal one at the same key shape, sharing the column.
    private static ReadOnlySpan<byte> PoisonedMarker => "poisoned"u8;

    [SkipLocalsInit]
    public void RecordClear(ulong block, scoped ReadOnlySpan<byte> accountKey, IWriteBatch batch)
    {
        Span<byte> key = stackalloc byte[accountKey.Length + BlockBytes];
        WriteClearKey(key, accountKey, block);
        batch.Set(key, Array.Empty<byte>());
    }

    /// <summary>Records a destruct over the per-slot enumeration cap: no pre-value rows were written, so reads
    /// below it must fail closed rather than silently omit slots.</summary>
    [SkipLocalsInit]
    public void RecordPoisonedClear(ulong block, scoped ReadOnlySpan<byte> accountKey, IWriteBatch batch)
    {
        Span<byte> key = stackalloc byte[accountKey.Length + BlockBytes];
        WriteClearKey(key, accountKey, block);
        batch.PutSpan(key, PoisonedMarker);
    }

    /// <summary>The lowest over-cap destruct recorded above <paramref name="afterBlockExclusive"/>, if any. A
    /// recorded row at or below it is authoritative; a row above it resolved its pre-value through a live column
    /// the destruct had already truncated.</summary>
    [SkipLocalsInit]
    public bool TryGetPoisonedClearAbove(scoped ReadOnlySpan<byte> accountKey, ulong afterBlockExclusive, out ulong clearBlock)
    {
        clearBlock = 0;
        if (afterBlockExclusive == ulong.MaxValue) return false;

        Span<byte> lowerBound = stackalloc byte[accountKey.Length + BlockBytes];
        WriteClearKey(lowerBound, accountKey, afterBlockExclusive + 1);

        Span<byte> upperBound = stackalloc byte[accountKey.Length + BlockBytes + 1];
        accountKey.CopyTo(upperBound);
        upperBound[accountKey.Length..].Fill(0xFF);

        using ISortedView view = _clears.GetViewBetween(lowerBound, upperBound);
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> foundKey = view.CurrentKey;
            if (foundKey.Length != accountKey.Length + BlockBytes || !foundKey[..accountKey.Length].SequenceEqual(accountKey))
                return false; // ran past this account's key range

            if (view.CurrentValue.SequenceEqual(PoisonedMarker))
            {
                clearBlock = BinaryPrimitives.ReadUInt64BigEndian(foundKey[accountKey.Length..]);
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether storage was cleared in <c>(afterBlockExclusive, atOrBeforeBlock]</c>. The lower bound is
    /// exclusive: a slot written in the destruct's own block is the resurrected value and must survive.</summary>
    [SkipLocalsInit]
    public bool HasClearInRange(scoped ReadOnlySpan<byte> accountKey, ulong afterBlockExclusive, ulong atOrBeforeBlock)
    {
        if (afterBlockExclusive >= atOrBeforeBlock) return false;

        int keyLen = accountKey.Length + BlockBytes;
        Span<byte> seekKey = stackalloc byte[keyLen];
        WriteClearKey(seekKey, accountKey, afterBlockExclusive + 1);

        Span<byte> upperBound = stackalloc byte[keyLen + 1];
        accountKey.CopyTo(upperBound);
        upperBound[accountKey.Length..].Fill(0xFF);
        upperBound[^1] = 0x00;

        Span<byte> foundKey = stackalloc byte[keyLen];
        if (!_clears.TryGetCeiling(seekKey, upperBound, foundKey, out int foundKeyLen, [], out _) || foundKeyLen != keyLen)
            return false;

        return BinaryPrimitives.ReadUInt64BigEndian(foundKey[accountKey.Length..]) <= atOrBeforeBlock;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteClearKey(Span<byte> destination, scoped ReadOnlySpan<byte> accountKey, ulong block)
    {
        accountKey.CopyTo(destination[..accountKey.Length]);
        BinaryPrimitives.WriteUInt64BigEndian(destination[accountKey.Length..], block);
    }
}
