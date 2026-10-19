// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>Sender accounts at the transaction boundary, before post-execution credits.</summary>
/// <remarks>Supports only the account and delegation reads used by inclusion-list validation.</remarks>
internal sealed class InclusionListState : IReadOnlyStateProvider
{
    private readonly Dictionary<AddressAsKey, (bool Exists, AccountStruct Account, bool Delegated)> _accounts = [];

    public InclusionListState(Transaction[] inclusionList, IReadOnlyStateProvider state)
    {
        foreach (Transaction tx in inclusionList)
        {
            if (tx.SupportsBlobs || tx.SupportsFrames || tx.SenderAddress is not { } sender || _accounts.ContainsKey(sender)) continue;
            bool exists = state.TryGetAccount(sender, out AccountStruct account);
            _accounts.Add(sender, (exists, account, account.HasCode && state.IsDelegatedCode(sender)));
        }
    }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        (bool exists, AccountStruct snapshot, _) = _accounts[address];
        account = snapshot;
        return exists;
    }

    public bool IsDelegatedCode(Address address) => _accounts[address].Delegated;
    public bool AccountExists(Address address) => _accounts[address].Exists;
    public bool IsContract(Address address) => _accounts[address].Account.HasCode;
    public bool IsDeadAccount(Address address) => !_accounts[address].Exists || _accounts[address].Account.IsEmpty;
    public Hash256 StateRoot => throw new NotSupportedException();
    public ReadOnlyMemory<byte> GetCode(Address address) => throw new NotSupportedException();
    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) => throw new NotSupportedException();
    public void Get(in StorageCell storageCell, out UInt256 value) => throw new NotSupportedException();
}
