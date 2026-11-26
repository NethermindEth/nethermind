// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Validators;

/// <summary>Restores the EIP-7805 account view before finalization from a validated block access list.</summary>
/// <remarks>The BAL must already be validated. State and account storage roots identify the backing finalized state.</remarks>
public sealed class InclusionListCommittedState(
    IReadOnlyStateProvider committed,
    uint finalizationIndex,
    Func<ReadOnlyBlockAccessList?> readList,
    Func<IReadOnlyStateProvider?> readParent) : IReadOnlyStateProvider
{
    private readonly Dictionary<AddressAsKey, (bool Exists, AccountStruct Account)> _accounts = [];
    private ReadOnlyBlockAccessList? _list;
    private bool _listRead;
    private IReadOnlyStateProvider? _parent;
    private bool _parentRead;

    /// <summary>Whether every requested value was available.</summary>
    public bool Available { get; private set; } = true;

    /// <inheritdoc/>
    public Hash256 StateRoot => committed.StateRoot;

    /// <inheritdoc/>
    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        if (_accounts.TryGetValue(address, out (bool Exists, AccountStruct Account) cached))
        {
            account = cached.Account;
            return cached.Exists;
        }
        if (List is not { } list)
        {
            account = AccountStruct.TotallyEmpty;
            return false;
        }

        bool exists = committed.TryGetAccount(address, out account);
        if (!exists) account = AccountStruct.TotallyEmpty;
        if (list.GetAccountChanges(address) is { } changes)
        {
            UInt256 balance = account.Balance;
            ulong nonce = account.Nonce;
            ValueHash256 codeHash = account.CodeHash;
            (bool Exists, AccountStruct Account)? parentAccount = null;
            if (changes.BalanceChangeAtIndex(finalizationIndex) is not null)
                balance = changes.GetBalance(finalizationIndex) ?? ParentAccount().Account.Balance;
            if (changes.NonceChangeAtIndex(finalizationIndex) is not null)
                nonce = changes.TryGetLastNonceChangeBefore(finalizationIndex, out NonceChange change)
                    ? change.Value : ParentAccount().Account.Nonce;
            if (changes.CodeChangeAtIndex(finalizationIndex) is not null)
                codeHash = changes.GetCodeHash(finalizationIndex) ?? ParentAccount().Account.CodeHash;
            account = new AccountStruct(nonce, balance, account.StorageRoot, codeHash);
            if (!changes.HasNoChangesAtIndex(finalizationIndex))
            {
                // EIP-161 removes empty accounts touched before finalization; untouched empty accounts may exist.
                exists = !account.IsEmpty || !HasPriorChanges(changes) && ParentAccount().Exists;
            }

            (bool Exists, AccountStruct Account) ParentAccount()
            {
                if (parentAccount is { } previousValue) return previousValue;
                AccountStruct previous = AccountStruct.TotallyEmpty;
                bool previousExists = Parent is { } parent && parent.TryGetAccount(address, out previous);
                if (!previousExists) previous = AccountStruct.TotallyEmpty;
                parentAccount = (previousExists, previous);
                return parentAccount.Value;
            }
        }
        _accounts.Add(address, (exists, account));
        return exists;
    }

    private bool HasPriorChanges(ReadOnlyAccountChanges changes)
    {
        if (changes.TryGetLastBalanceChangeBefore(finalizationIndex, out _)
            || changes.TryGetLastNonceChangeBefore(finalizationIndex, out _)
            || changes.TryGetLastCodeChangeBefore(finalizationIndex, out _)) return true;
        foreach (ReadOnlySlotChanges slot in changes.StorageChanges)
            if (slot.TryGetLastBefore(finalizationIndex, out _)) return true;
        return false;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> GetCode(Address address)
    {
        TryGetAccount(address, out AccountStruct account);
        return account.HasCode ? GetCode(account.CodeHash) : Array.Empty<byte>();
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
    {
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256) return Array.Empty<byte>();
        if (List?.GetCodeChangesByHash() is { } codes && codes.TryGetValue(codeHash, out (uint Index, byte[] Code) change)
            && change.Index < finalizationIndex) return change.Code;
        ReadOnlyMemory<byte> code = committed.GetCode(in codeHash);
        if (code.Equals(default)) Available = false;
        return code;
    }

    /// <inheritdoc/>
    public bool IsContract(Address address) => TryGetAccount(address, out AccountStruct account) && account.HasCode;
    /// <inheritdoc/>
    public bool AccountExists(Address address) => TryGetAccount(address, out _);
    /// <inheritdoc/>
    public bool IsDeadAccount(Address address) => !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;

    /// <inheritdoc/>
    public void Get(in StorageCell storageCell, out UInt256 value)
    {
        value = default;
        if (List is not { } list) return;
        ReadOnlyAccountChanges? account = list.GetAccountChanges(storageCell.Address);
        if (account is not null && account.TryGetDeclaredSlotChanges(storageCell.Index, out ReadOnlySlotChanges? slot)
            && slot is not null && slot.TryGetLastBefore(finalizationIndex + 1, out StorageChange last)
            && last.Index == finalizationIndex)
        {
            if (slot.TryGetLastBefore(finalizationIndex, out StorageChange previous)) value = previous.Value;
            else Parent?.Get(in storageCell, out value);
        }
        else committed.Get(in storageCell, out value);
    }

    private ReadOnlyBlockAccessList? List
    {
        get
        {
            if (!_listRead)
            {
                _list = readList();
                _listRead = true;
                if (_list is null) Available = false;
            }
            return _list;
        }
    }

    private IReadOnlyStateProvider? Parent
    {
        get
        {
            if (!_parentRead)
            {
                _parent = readParent();
                _parentRead = true;
                if (_parent is null) Available = false;
            }
            return _parent;
        }
    }
}
