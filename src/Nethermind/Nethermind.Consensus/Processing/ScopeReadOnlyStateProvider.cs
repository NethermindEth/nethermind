// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>Read-only access to a pinned parent scope owned by the caller.</summary>
internal sealed class ScopeReadOnlyStateProvider(IWorldStateScopeProvider.IScope scope) : IReadOnlyStateProvider
{
    public Hash256 StateRoot => scope.RootHash;

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        Account? value = scope.Get(address);
        account = value?.ToStruct() ?? AccountStruct.TotallyEmpty;
        return value is not null;
    }

    public ReadOnlyMemory<byte> GetCode(Address address)
    {
        TryGetAccount(address, out AccountStruct account);
        return account.HasCode ? GetCode(account.CodeHash) : Array.Empty<byte>();
    }

    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) => scope.CodeDb.GetCode(in codeHash);
    public bool AccountExists(Address address) => TryGetAccount(address, out _);
    public bool IsContract(Address address) => TryGetAccount(address, out AccountStruct account) && account.HasCode;
    public bool IsDeadAccount(Address address) => !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;
    public void Get(in StorageCell storageCell, out UInt256 value)
        => scope.CreateStorageTree(storageCell.Address).Get(storageCell.Index, out value);
}
