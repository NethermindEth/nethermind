// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Validators;

/// <summary>A stored block's state right after its transactions, rebuilt from its block access list.</summary>
/// <remarks>An account the list declares is the parent's, overlaid with the list's last change before the
/// post-execution index, so withdrawals and post-execution system calls are not counted. Any other account was not
/// changed by the block and is read from <paramref name="postBlockState"/>.</remarks>
public sealed class PostTransactionsStateProvider(
    IReadOnlyStateProvider postBlockState,
    IReadOnlyStateProvider parentState,
    ReadOnlyBlockAccessList blockAccessList,
    int transactionCount) : IReadOnlyStateProvider
{
    private readonly uint _postExecutionIndex = (uint)transactionCount + 1;

    public Hash256 StateRoot => throw new NotSupportedException("The state root after the transactions is not computed.");

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        if (blockAccessList.GetAccountChanges(address) is not { } changes) return postBlockState.TryGetAccount(address, out account);

        bool exists = parentState.TryGetAccount(address, out AccountStruct parent);
        bool balanceChanged = changes.TryGetLastBalanceChangeBefore(_postExecutionIndex, out BalanceChange balance);
        bool nonceChanged = changes.TryGetLastNonceChangeBefore(_postExecutionIndex, out NonceChange nonce);
        bool codeChanged = changes.TryGetLastCodeChangeBefore(_postExecutionIndex, out CodeChange code);
        account = new AccountStruct(
            nonceChanged ? nonce.Value : parent.Nonce,
            balanceChanged ? balance.Value : parent.Balance,
            parent.StorageRoot,
            codeChanged ? code.CodeHash : parent.CodeHash);
        return balanceChanged || nonceChanged || codeChanged ? !account.IsEmpty : exists;
    }

    public ReadOnlyMemory<byte> GetCode(Address address)
    {
        if (blockAccessList.GetAccountChanges(address) is not { } changes) return postBlockState.GetCode(address);
        if (!changes.TryGetLastCodeChangeBefore(_postExecutionIndex, out CodeChange code)) return parentState.GetCode(address);
        return code.IsAdopted ? postBlockState.GetCode(code.CodeHash) : code.Code;
    }

    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) => postBlockState.GetCode(in codeHash);

    public bool IsContract(Address address) => TryGetAccount(address, out AccountStruct account) && account.IsContract;

    public bool AccountExists(Address address) => TryGetAccount(address, out _);

    public bool IsDeadAccount(Address address) => !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;

    public void Get(in StorageCell storageCell, out UInt256 value)
    {
        if (blockAccessList.GetAccountChanges(storageCell.Address) is not { } changes)
        {
            postBlockState.Get(in storageCell, out value);
            return;
        }

        if (changes.TryGetDeclaredSlotChanges(storageCell.Index, out ReadOnlySlotChanges? slot)
            && slot is not null
            && slot.TryGetLastBefore(_postExecutionIndex, out StorageChange change))
        {
            value = change.Value;
            return;
        }

        parentState.Get(in storageCell, out value);
    }
}
