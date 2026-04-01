// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.State;

public class TracedAccessWorldState(IWorldState innerWorldState) : WrappedWorldState(innerWorldState), IPreBlockCaches
{
    private readonly BlockAccessList _generatingBlockAccessList = new();
    public PreBlockCaches Caches => (_innerWorldState as IPreBlockCaches).Caches;
    public bool IsWarmWorldState => (_innerWorldState as IPreBlockCaches)?.IsWarmWorldState ?? false;

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        _innerWorldState.AddToBalance(address, balanceChange, spec, out oldBalance);

        UInt256 newBalance = oldBalance + balanceChange;
        _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        bool res = _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);
        UInt256 newBalance = oldBalance + balanceChange;
        _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);

        return res;
    }

    public override IDisposable BeginScope(BlockHeader? baseBlock)
        => _innerWorldState.BeginScope(baseBlock);

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        _generatingBlockAccessList.AddStorageRead(storageCell);
        return GetInternal(storageCell);
    }

    public override byte[] GetOriginal(in StorageCell storageCell)
    {
        _generatingBlockAccessList.AddStorageRead(storageCell);
        return _innerWorldState.GetOriginal(storageCell);
    }

    public override void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        _innerWorldState.IncrementNonce(address, delta, out oldNonce);
        _generatingBlockAccessList.AddNonceChange(address, (ulong)(oldNonce + delta));
    }

    public override void SetNonce(Address address, in UInt256 nonce)
    {
        _innerWorldState.SetNonce(address, nonce);
        _generatingBlockAccessList.AddNonceChange(address, (ulong)nonce);
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        byte[] oldCode = GetCodeInternal(address) ?? [];
        _generatingBlockAccessList.AddCodeChange(address, oldCode, code);
        return _innerWorldState.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        ReadOnlySpan<byte> oldValue = GetInternal(storageCell);
        _generatingBlockAccessList.AddStorageChange(storageCell, new(oldValue, true), new(newValue, true));
        _innerWorldState.Set(storageCell, newValue);
    }

    public override UInt256 GetBalance(Address address)
    {
        AddAccountRead(address);
        return GetBalanceInternal(address);
    }

    public override UInt256 GetNonce(Address address)
    {
        AddAccountRead(address);
        return GetNonceInternal(address);
    }

    public override ValueHash256 GetCodeHash(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.GetCodeHash(address);
    }

    public override byte[]? GetCode(Address address)
    {
        AddAccountRead(address);
        return GetCodeInternal(address);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        oldBalance = 0;

        if (address == Address.SystemUser && balanceChange.IsZero)
        {
            return;
        }

        oldBalance = GetBalanceInternal(address);

        UInt256 newBalance = oldBalance - balanceChange;
        _generatingBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);

        _innerWorldState.SubtractFromBalance(address, balanceChange, spec, out oldBalance);
    }

    public override void DeleteAccount(Address address)
    {
        _generatingBlockAccessList.DeleteAccount(address, GetBalanceInternal(address));
        _innerWorldState.DeleteAccount(address);
    }

    public override void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        AddAccountRead(address);
        if (!balance.IsZero)
        {
            _generatingBlockAccessList.AddBalanceChange(address, 0, balance);
        }
        if (!nonce.IsZero)
        {
            _generatingBlockAccessList.AddNonceChange(address, (ulong)nonce);
        }

        _innerWorldState.CreateAccount(address, balance, nonce);
    }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        AddAccountRead(address);
        _innerWorldState.CreateAccountIfNotExists(address, balance, nonce);
    }

    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        AddAccountRead(address);
        account = GetAccountInternal(address) ?? AccountStruct.TotallyEmpty;
        return !account.IsTotallyEmpty;
    }

    public void AddAccountRead(Address address)
        => _generatingBlockAccessList.AddAccountRead(address);
    
    public void SetIndex(int index)
        => _generatingBlockAccessList.Index = index;
    
    public void IncrementIndex()
        => _generatingBlockAccessList.Index++;
    
    public void Clear()
        => _generatingBlockAccessList.Clear();
    
    public void MergeGeneratingBal(BlockAccessList other)
        => other.Merge(_generatingBlockAccessList);

    public override void Restore(Snapshot snapshot)
    {
        _generatingBlockAccessList.Restore(snapshot.BlockAccessListSnapshot);
        _innerWorldState.Restore(snapshot);
    }

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int blockAccessListSnapshot = _generatingBlockAccessList.TakeSnapshot();
        Snapshot snapshot = _innerWorldState.TakeSnapshot(newTransactionStart);
        return new(snapshot.StorageSnapshot, snapshot.StateSnapshot, blockAccessListSnapshot);
    }

    public override bool AccountExists(Address address)
    {
        AddAccountRead(address);
        return AccountExistsInternal(address);
    }

    public override bool IsContract(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.IsContract(address);
    }

    public override bool IsStorageEmpty(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.IsStorageEmpty(address);
    }

    public override bool IsDeadAccount(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.IsDeadAccount(address);
    }

    public override void ClearStorage(Address address)
    {
        AddAccountRead(address);
        _innerWorldState.ClearStorage(address);
    }

    private UInt256 GetBalanceInternal(Address address)
    {
        AccountChanges? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        if (accountChanges is not null && accountChanges.BalanceChanges.Count == 1)
        {
            return accountChanges.BalanceChanges.First().PostBalance;
        }
        return _innerWorldState.GetBalance(address);
    }

    private UInt256 GetNonceInternal(Address address)
    {
        AccountChanges? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        if (accountChanges is not null && accountChanges.NonceChanges.Count == 1)
        {
            return accountChanges.NonceChanges.First().NewNonce;
        }

        return _innerWorldState.GetNonce(address);
    }

    private byte[]? GetCodeInternal(Address address)
    {
        AccountChanges? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        if (accountChanges is not null && accountChanges.CodeChanges.Count == 1)
        {
            return accountChanges.CodeChanges.First().NewCode;
        }

        return _innerWorldState.GetCode(address);
    }

    private ReadOnlySpan<byte> GetInternal(in StorageCell storageCell)
    {
        AccountChanges? accountChanges = _generatingBlockAccessList.GetAccountChanges(storageCell.Address);
        accountChanges.TryGetSlotChanges(storageCell.Index, out SlotChanges? slotChanges);

        if (slotChanges is not null && slotChanges.Changes.Count == 1)
        {
            return slotChanges.Changes.First().Value.NewValue.ToBigEndian();
        }

        return _innerWorldState.Get(storageCell);
    }

    private bool AccountExistsInternal(Address address)
    {
        AccountChanges? accountChanges = _generatingBlockAccessList.GetAccountChanges(address);
        if (accountChanges is not null && accountChanges.NonceChanges.Count == 1)
        {
            // if nonce is changed in this tx must exist
            // could have been created this tx
            return true;
        }

        return _innerWorldState.AccountExists(address);
    }

    private AccountStruct? GetAccountInternal(Address address)
        => AccountExistsInternal(address) ? new(
                GetNonceInternal(address),
                GetBalanceInternal(address),
                Keccak.EmptyTreeHash, // never used
                _innerWorldState.GetCodeHash(address)) : null;

}
