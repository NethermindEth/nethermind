// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Restores the EIP-7805 account view before finalization from a validated block access list.</summary>
/// <remarks>Only inclusion-list account checks are supported; storage is not reconstructed.</remarks>
internal sealed class InclusionListCommittedState(
    IReadOnlyStateProvider committed,
    uint finalizationIndex,
    Func<ReadOnlyBlockAccessList?> readList,
    Func<IReadOnlyStateProvider?> readParent) : IReadOnlyStateProvider
{
    private readonly Dictionary<AddressAsKey, AccountStruct> _accounts = [];
    private ReadOnlyBlockAccessList? _list;
    private bool _listRead;
    private IReadOnlyStateProvider? _parent;
    private bool _parentRead;

    public bool Available { get; private set; } = true;
    public Hash256 StateRoot => committed.StateRoot;

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        if (_accounts.TryGetValue(address, out account)) return !account.IsEmpty;
        if (!_listRead)
        {
            _list = readList();
            _listRead = true;
        }
        if (_list is null)
        {
            Available = false;
            account = AccountStruct.TotallyEmpty;
            return false;
        }

        if (!committed.TryGetAccount(address, out account)) account = AccountStruct.TotallyEmpty;
        if (_list.GetAccountChanges(address) is { } changes)
        {
            UInt256 balance = account.Balance;
            ulong nonce = account.Nonce;
            ValueHash256 codeHash = account.CodeHash;
            AccountStruct? parentAccount = null;
            if (changes.BalanceChangeAtIndex(finalizationIndex) is not null)
                balance = changes.GetBalance(finalizationIndex) ?? ParentAccount().Balance;
            if (changes.NonceChangeAtIndex(finalizationIndex) is not null)
                nonce = changes.TryGetLastNonceChangeBefore(finalizationIndex, out NonceChange change)
                    ? change.Value : ParentAccount().Nonce;
            if (changes.CodeChangeAtIndex(finalizationIndex) is not null)
                codeHash = changes.GetCodeHash(finalizationIndex) ?? ParentAccount().CodeHash;
            account = new AccountStruct(nonce, balance, account.StorageRoot, codeHash);

            AccountStruct ParentAccount()
            {
                if (parentAccount is { } cached) return cached;
                if (!_parentRead)
                {
                    _parent = readParent();
                    _parentRead = true;
                }
                AccountStruct previous = AccountStruct.TotallyEmpty;
                if (_parent is null) Available = false;
                else if (!_parent.TryGetAccount(address, out previous)) previous = AccountStruct.TotallyEmpty;
                parentAccount = previous;
                return previous;
            }
        }
        _accounts.Add(address, account);
        return !account.IsEmpty;
    }

    public ReadOnlyMemory<byte> GetCode(Address address)
    {
        TryGetAccount(address, out AccountStruct account);
        return account.HasCode ? GetCode(account.CodeHash) : Array.Empty<byte>();
    }

    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
    {
        ReadOnlyMemory<byte> code = committed.GetCode(in codeHash);
        if (code.Equals(default)) Available = false;
        return code;
    }

    public bool IsContract(Address address) => TryGetAccount(address, out AccountStruct account) && account.HasCode;
    public bool AccountExists(Address address) => TryGetAccount(address, out _);
    public bool IsDeadAccount(Address address) => !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;
    public void Get(in StorageCell storageCell, out UInt256 value) => throw new NotSupportedException();
}
