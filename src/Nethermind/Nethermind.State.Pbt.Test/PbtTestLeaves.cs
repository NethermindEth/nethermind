// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Test;

/// <summary>Builds leaf rebuild entries and reads persisted typed state for reference-tree tests.</summary>
internal static class PbtTestLeaves
{
    public static Account? ReadAccount(IPbtPersistence.IReader reader, Address address) =>
        reader.GetAccount(PbtKeyDerivation.AddressKeyHash(address))?.ToAccount();

    /// <summary>The account's stem without its bytecode: a zero code size under its code-hash leaf.</summary>
    public static PbtAccount ToPbtAccount(this Account account)
    {
        ValueHash256 basicData = default;
        PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, 0, account.Nonce, account.Balance);
        return new PbtAccount(basicData, account.CodeHash.ValueHash256, IsDelegation: false);
    }

    public static byte[] Encoded(this PbtAccount account)
    {
        byte[] encoded = new byte[account.EncodedLength];
        account.Encode(encoded);
        return encoded;
    }

    public static EvmWord ReadSlot(IPbtPersistence.IReader reader, Address address, in UInt256 slot) =>
        reader.GetSlot(PbtStateKey.Storage(address, slot));

    public static void AddAccount(List<RebuildEntry> into, Address address, in Account account, byte[]? code)
    {
        foreach ((PbtPath key, ValueHash256 leaf) in PbtFlatState.AccountLeaves(
            PbtKeyDerivation.AddressKeyHash(address), account, code is { Length: > 0 } ? new CodeInfo(code) : null))
            into.Add(new RebuildEntry((PbtStorageTreeKey)key, leaf));
    }

    public static void AddSlot(List<RebuildEntry> into, Address address, in UInt256 slot, in UInt256 value) =>
        into.Add(new RebuildEntry(PbtStateKey.Storage(address, slot), new ValueHash256(value.ToBigEndian())));

    /// <summary>A snapshot root calculation that consumes the written leaves and claims <paramref name="root"/> whatever they hold.</summary>
    public static Func<IEnumerable<RebuildEntry>, ValueHash256> Claiming(ValueHash256 root) => leaves =>
    {
        foreach (RebuildEntry _ in leaves) { }
        return root;
    };

    public static PbtStorageTreeKey StoragePrefix(Address address) =>
        new([Eip8297KeyDerivation.StorageZone, .. PbtKeyDerivation.AddressKeyHash(address).Bytes]);

    public static IEnumerable<KeyValuePair<PbtStorageTreeKey, ValueHash256>> EnumerateLeaves(this PbtReadOnlySnapshotBundle bundle, PbtStorageTreeKey prefix) =>
        bundle.EnumerateLeaves().Where(leaf => leaf.Key.Bytes.StartsWith(prefix.Bytes));

    public static IEnumerable<KeyValuePair<PbtStorageTreeKey, ValueHash256>> EnumerateLeaves(this PbtReadOnlySnapshotBundle bundle)
    {
        SortedDictionary<PbtStorageTreeKey, ValueHash256> leaves = [];
        foreach ((ValueHash256 addressHash, PbtAccount stem) in bundle.EnumerateAccounts())
        {
            Account account = stem.ToAccount();
            foreach ((PbtPath key, ValueHash256 value) in PbtFlatState.AccountLeaves(addressHash, account, account.HasCode ? bundle.GetCode(account.CodeHash.ValueHash256) : null))
                leaves[(PbtStorageTreeKey)key] = value;
        }
        foreach ((PbtStorageTreeKey key, EvmWord value) in bundle.EnumerateStorage())
            if (!EvmWordSlot.IsZero(value)) leaves[key] = new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value));
        return leaves;
    }
}
