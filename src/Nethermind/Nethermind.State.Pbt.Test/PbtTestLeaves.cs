// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;

namespace Nethermind.State.Pbt.Test;

/// <summary>Builds leaf rebuild entries and reads persisted typed state for reference-tree tests.</summary>
internal static class PbtTestLeaves
{
    public static Account? ReadAccount(IPbtPersistence.IReader reader, Address address) =>
        reader.GetAccount(Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes))?.ToAccount();

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

    public static PbtVariableTreeKey SlotKey(Address address, in UInt256 slot)
    {
        ValueHash256 address32 = address.ToHash();
        return Eip8297KeyDerivation.SlotKey(address32.Bytes, Eip8297KeyDerivation.AddressHash(address32.Bytes), slot);
    }

    public static UInt256 ReadSlot(IPbtPersistence.IReader reader, Address address, in UInt256 slot) =>
        reader.GetSlot(SlotKey(address, slot));

    public static void AddAccount(List<RebuildEntry> into, Address address, in Account account, byte[]? code)
    {
        using ArrayPoolList<KeyValuePair<PbtPath, ValueHash256>> accountLeaves = PbtStateKey.AccountLeaves(
            Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes), account, code is { Length: > 0 } ? new CodeInfo(code) : null);
        foreach ((PbtPath key, ValueHash256 leaf) in accountLeaves)
            into.Add(new RebuildEntry((PbtVariableTreeKey)key, leaf));
    }

    public static void AddSlot(List<RebuildEntry> into, Address address, in UInt256 slot, in UInt256 value) =>
        into.Add(new RebuildEntry(SlotKey(address, slot), value.ToValueHash()));

    /// <summary>Reads every account preimage and its slot preimages from <paramref name="source"/>.</summary>
    public static List<PbtAccountPreimages> ReadPreimages(Stream source)
    {
        List<PbtAccountPreimages> accounts = [];
        PbtPreimageReader reader = new(source);
        while (reader.ReadAccount(out Address? address, out uint count, CancellationToken.None))
        {
            List<ValueHash256> slots = [];
            for (uint index = 0; index < count; index++) slots.Add(reader.ReadSlot(CancellationToken.None));
            accounts.Add(new(address!, count, slots));
        }
        return accounts;
    }

    /// <summary>A snapshot root calculation that consumes the written leaves and claims <paramref name="root"/> whatever they hold.</summary>
    public static Func<IEnumerable<RebuildEntry>, ValueHash256> Claiming(ValueHash256 root) => leaves =>
    {
        foreach (RebuildEntry _ in leaves) { }
        return root;
    };

    /// <summary>A database factory that places every database in <paramref name="directory"/>.</summary>
    public static IDbFactory ScratchDbFactory(string directory)
    {
        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns(directory);
        return dbFactory;
    }

    public static PbtLeafIngestion LeafIngestion(PbtRocksDbPersistence target, IPbtConfig config, string scratchDirectory) =>
        new(target, new PbtRebuilder(target, config, config.ImportConcurrency, LimboLogs.Instance), ScratchDbFactory(scratchDirectory), config, LimboLogs.Instance);
}
