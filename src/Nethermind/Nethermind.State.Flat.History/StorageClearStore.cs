// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Finalized-only storage-clear events (<c>[account key | block BE] -> empty</c>), one per self-destruct that had
/// persisted storage. The live flat column expresses such a destruct as a range-delete over the account's slots,
/// which leaves no per-slot tombstones in <see cref="HistoryStore"/>; this column records the event so an as-of
/// read can tell that a slot value written before the destruct is dead.
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

    // Distinguishes a poisoned clear (over the v3 per-slot destruct cap) from a normal one (empty value) at the
    // same key shape, so HasClearInRange (unaware of poisoning, used by both v2 and v3's normal path) and
    // HasPoisonedClearAbove (v3-only, fail-closed path) can share one column without a schema change.
    private static ReadOnlySpan<byte> PoisonedMarker => "poisoned"u8;

    [SkipLocalsInit]
    public void RecordClear(ulong block, scoped ReadOnlySpan<byte> accountKey, IWriteBatch batch)
    {
        Span<byte> key = stackalloc byte[accountKey.Length + BlockBytes];
        WriteClearKey(key, accountKey, block);
        batch.Set(key, Array.Empty<byte>());
    }

    /// <summary>
    /// Records a destruct whose persisted storage exceeded the v3 per-slot enumeration cap: no per-slot pre-value
    /// rows were written for it, so a v3 read below this block for this account cannot be answered correctly and
    /// must fail closed instead of silently omitting slots — see <see cref="HasPoisonedClearAbove"/>.
    /// </summary>
    [SkipLocalsInit]
    public void RecordPoisonedClear(ulong block, scoped ReadOnlySpan<byte> accountKey, IWriteBatch batch)
    {
        Span<byte> key = stackalloc byte[accountKey.Length + BlockBytes];
        WriteClearKey(key, accountKey, block);
        batch.PutSpan(key, PoisonedMarker);
    }

    /// <summary>Whether a poisoned (over-cap) destruct is recorded for this account at any block strictly above
    /// <paramref name="afterBlockExclusive"/> — the v3 storage read path's fail-closed check.</summary>
    [SkipLocalsInit]
    public bool HasPoisonedClearAbove(scoped ReadOnlySpan<byte> accountKey, ulong afterBlockExclusive)
    {
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

            if (view.CurrentValue.SequenceEqual(PoisonedMarker)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the account's storage was cleared in <c>(afterBlockExclusive, atOrBeforeBlock]</c>. The lower bound
    /// is exclusive because a slot written in the same block as a destruct is the post-destruct (resurrected)
    /// value and must survive, mirroring the live column's destruct-then-write batch order.
    /// </summary>
    [SkipLocalsInit]
    public bool HasClearInRange(scoped ReadOnlySpan<byte> accountKey, ulong afterBlockExclusive, ulong atOrBeforeBlock)
    {
        if (afterBlockExclusive >= atOrBeforeBlock) return false;

        // Clear keys ascend by block - find earliest clear after the lower bound and check it is at/before upper bound
        int keyLen = accountKey.Length + BlockBytes;
        Span<byte> seekKey = stackalloc byte[keyLen];
        WriteClearKey(seekKey, accountKey, afterBlockExclusive + 1);

        // One byte past [account | 0xFF..FF], so the bound stops at the account without needing atOrBeforeBlock + 1.
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
