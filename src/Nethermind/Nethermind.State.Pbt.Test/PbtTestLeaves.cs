// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
        reader.GetAccount(PbtKeyDerivation.AddressKeyHash(address));

    public static EvmWord ReadSlot(IPbtPersistence.IReader reader, Address address, in UInt256 slot) =>
        reader.GetSlot(PbtStateKey.Storage(address, slot));

    public static void AddAccount(List<RebuildEntry> into, Address address, in Account account, byte[]? code)
    {
        foreach ((PbtFullKey key, ValueHash256 leaf) in PbtFlatState.AccountLeaves(
            PbtKeyDerivation.AddressKeyHash(address), account, code is { Length: > 0 } ? new CodeInfo(code) : null))
            into.Add(new RebuildEntry((PbtStorageFullKey)key, leaf));
    }

    public static void AddSlot(List<RebuildEntry> into, Address address, in UInt256 slot, in UInt256 value) =>
        into.Add(new RebuildEntry(PbtStateKey.Storage(address, slot), new ValueHash256(value.ToBigEndian())));
}
