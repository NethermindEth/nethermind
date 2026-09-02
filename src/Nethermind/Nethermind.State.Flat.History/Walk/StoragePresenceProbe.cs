// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class StoragePresenceProbe(ISortedKeyValueStore storageHistory, ISortedKeyValueStore storageClears, HistoryRowFormat rowFormat)
{
    private const int IdentityLength = BaseFlatPersistence.AccountKeyLength;
    private const int PrefixLength = BasePersistence.StoragePrefixPortion;
    private const int SuffixOffset = PrefixLength + Hash256.Size;
    private const int SuffixLength = IdentityLength - PrefixLength;
    private const int StorageRowKeyLength = BaseFlatPersistence.StorageKeyLength + sizeof(ulong);
    private const int ClearRowKeyLength = Hash256.Size + sizeof(ulong);

    private readonly Dictionary<ValueHash256, bool> _memo = [];

    public bool HasStorageHistory(in ValueHash256 accountPath, ulong to)
    {
        ValueHash256 identity = default;
        accountPath.Bytes[..IdentityLength].CopyTo(identity.BytesAsSpan);
        if (_memo.TryGetValue(identity, out bool known)) return known;

        bool has = HasClear(identity, to) || HasSlotRow(identity, to);
        _memo[identity] = has;
        return has;
    }

    private bool HasClear(in ValueHash256 identity, ulong to)
    {
        Span<byte> lower = stackalloc byte[ClearRowKeyLength];
        lower.Clear();
        identity.Bytes[..IdentityLength].CopyTo(lower);
        Span<byte> upper = stackalloc byte[ClearRowKeyLength + 1];
        upper.Fill(0xFF);
        identity.Bytes[..IdentityLength].CopyTo(upper);
        upper[^1] = 0x00;

        using ISortedView view = storageClears.GetViewBetween(lower, upper);
        while (view.MoveNext())
        {
            if (view.CurrentKey.Length != ClearRowKeyLength) continue;

            return BinaryPrimitives.ReadUInt64BigEndian(view.CurrentKey[Hash256.Size..]) <= to;
        }

        return false;
    }

    private bool HasSlotRow(in ValueHash256 identity, ulong to)
    {
        Span<byte> lower = stackalloc byte[StorageRowKeyLength];
        lower.Clear();
        identity.Bytes[..PrefixLength].CopyTo(lower);
        Span<byte> upper = stackalloc byte[StorageRowKeyLength + 1];
        upper.Fill(0xFF);
        identity.Bytes[..PrefixLength].CopyTo(upper);
        upper[^1] = 0x00;

        using ISortedView view = storageHistory.GetViewBetween(lower, upper, ReadFlags.HintCacheMiss);
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != StorageRowKeyLength) continue;
            if (!key.Slice(SuffixOffset, SuffixLength).SequenceEqual(identity.Bytes.Slice(PrefixLength, SuffixLength))) continue;

            if (rowFormat.DecodeSuffixBlock(key[BaseFlatPersistence.StorageKeyLength..]) <= to) return true;
        }

        return false;
    }
}
