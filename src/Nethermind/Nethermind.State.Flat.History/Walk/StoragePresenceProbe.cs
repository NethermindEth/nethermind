// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class StoragePresenceProbe(ISortedKeyValueStore storageHistory)
{
    public const long MaxScannedRows = 1_000_000;

    private const int IdentityLength = BaseFlatPersistence.AccountKeyLength;
    private const int PrefixLength = BasePersistence.StoragePrefixPortion;
    private const int SuffixOffset = PrefixLength + Hash256.Size;
    private const int SuffixLength = IdentityLength - PrefixLength;
    private const int StorageRowKeyLength = BaseFlatPersistence.StorageKeyLength + sizeof(ulong);

    private readonly Dictionary<ValueHash256, bool> _memo = [];

    public bool HasSlotRows(in ValueHash256 accountPath)
    {
        ValueHash256 identity = default;
        accountPath.Bytes[..IdentityLength].CopyTo(identity.BytesAsSpan);
        if (_memo.TryGetValue(identity, out bool known)) return known;

        bool has = Scan(identity);
        _memo[identity] = has;
        return has;
    }

    private bool Scan(in ValueHash256 identity)
    {
        Span<byte> lower = stackalloc byte[StorageRowKeyLength];
        lower.Clear();
        identity.Bytes[..PrefixLength].CopyTo(lower);
        Span<byte> upper = stackalloc byte[StorageRowKeyLength + 1];
        upper.Fill(0xFF);
        identity.Bytes[..PrefixLength].CopyTo(upper);
        upper[^1] = 0x00;

        long scanned = 0;
        using ISortedView view = storageHistory.GetViewBetween(lower, upper, ReadFlags.HintCacheMiss);
        while (view.MoveNext())
        {
            if (++scanned > MaxScannedRows) return true;

            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != StorageRowKeyLength) continue;
            if (key.Slice(SuffixOffset, SuffixLength).SequenceEqual(identity.Bytes.Slice(PrefixLength, SuffixLength))) return true;
        }

        return false;
    }
}
