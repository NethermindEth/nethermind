// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
public sealed class StorageClearStore
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

    [SkipLocalsInit]
    public void RecordClear(ulong block, scoped ReadOnlySpan<byte> accountKey, IWriteBatch batch)
    {
        Span<byte> key = stackalloc byte[accountKey.Length + BlockBytes];
        WriteClearKey(key, accountKey, block);
        batch.Set(key, Array.Empty<byte>());
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

        Span<byte> upperKey = stackalloc byte[accountKey.Length + BlockBytes];
        WriteClearKey(upperKey, accountKey, atOrBeforeBlock);
        if (_clears.KeyExists(upperKey)) return true;

        Span<byte> lowerBound = stackalloc byte[accountKey.Length + BlockBytes];
        WriteClearKey(lowerBound, accountKey, afterBlockExclusive + 1);

        using ISortedView view = _clears.GetViewBetween(lowerBound, upperKey);
        if (!view.StartBefore(upperKey)) return false;

        ReadOnlySpan<byte> foundKey = view.CurrentKey;
        return foundKey.Length == accountKey.Length + BlockBytes && foundKey[..accountKey.Length].SequenceEqual(accountKey);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteClearKey(Span<byte> destination, scoped ReadOnlySpan<byte> accountKey, ulong block)
    {
        accountKey.CopyTo(destination[..accountKey.Length]);
        BinaryPrimitives.WriteUInt64BigEndian(destination[accountKey.Length..], block);
    }
}
