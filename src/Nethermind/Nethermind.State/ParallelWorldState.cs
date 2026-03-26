// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

public class ParallelWorldState(IWorldState innerWorldState, IBlockAccessListBuilder balStore) : WrappedWorldState(innerWorldState), IPreBlockCaches
{
    private readonly IBlockAccessListBuilder _balStore = balStore;

    public PreBlockCaches? Caches => (_innerWorldState as IPreBlockCaches)?.Caches;

    public bool IsWarmWorldState => (_innerWorldState as IPreBlockCaches)?.IsWarmWorldState ?? false;

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => AddToBalance(address, balanceChange, spec, out _);

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        _innerWorldState.AddToBalance(address, balanceChange, spec, out oldBalance);

        if (_balStore.TracingEnabled)
        {
            UInt256 newBalance = oldBalance + balanceChange;
            _balStore.GeneratedBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        bool res = _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);

        if (_balStore.TracingEnabled)
        {
            UInt256 newBalance = oldBalance + balanceChange;
            _balStore.GeneratedBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }

        return res;
    }

    public override IDisposable BeginScope(BlockHeader? baseBlock)
        => _innerWorldState.BeginScope(baseBlock);

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        if (_balStore.TracingEnabled)
        {
            _balStore.GeneratedBlockAccessList.AddStorageRead(storageCell);
        }
        return _innerWorldState.Get(storageCell);
    }

    public override void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        _innerWorldState.IncrementNonce(address, delta, out oldNonce);

        if (_balStore.TracingEnabled)
        {
            _balStore.GeneratedBlockAccessList.AddNonceChange(address, (ulong)(oldNonce + delta));
        }
    }

    public override void SetNonce(Address address, in UInt256 nonce)
    {
        _innerWorldState.SetNonce(address, nonce);

        if (_balStore.TracingEnabled)
        {
            _balStore.GeneratedBlockAccessList.AddNonceChange(address, (ulong)nonce);
        }
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        if (_balStore.TracingEnabled)
        {
            byte[] oldCode = _innerWorldState.GetCode(address) ?? Array.Empty<byte>();
            _balStore.GeneratedBlockAccessList.AddCodeChange(address, oldCode, code);
        }
        return _innerWorldState.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        if (_balStore.TracingEnabled)
        {
            ReadOnlySpan<byte> oldValue = _innerWorldState.Get(storageCell);
            _balStore.GeneratedBlockAccessList.AddStorageChange(storageCell, new(oldValue, true), new(newValue, true));
        }
        _innerWorldState.Set(storageCell, newValue);
    }

    public override ref readonly UInt256 GetBalance(Address address)
    {
        _balStore.AddAccountRead(address);
        return ref _innerWorldState.GetBalance(address);
    }

    public override ref readonly ValueHash256 GetCodeHash(Address address)
    {
        _balStore.AddAccountRead(address);
        return ref _innerWorldState.GetCodeHash(address);
    }

    public override byte[]? GetCode(Address address)
    {
        _balStore.AddAccountRead(address);
        return _innerWorldState.GetCode(address);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        _innerWorldState.SubtractFromBalance(address, balanceChange, spec, out oldBalance);

        if (_balStore.TracingEnabled)
        {
            UInt256 newBalance = oldBalance - balanceChange;
            _balStore.GeneratedBlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }
    }

    public override void DeleteAccount(Address address)
    {
        if (_balStore.TracingEnabled)
        {
            _balStore.GeneratedBlockAccessList.DeleteAccount(address, _innerWorldState.GetBalance(address));
        }
        _innerWorldState.DeleteAccount(address);
    }

    public override void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        if (_balStore.TracingEnabled)
        {
            _balStore.GeneratedBlockAccessList.AddAccountRead(address);
            if (!balance.IsZero)
            {
                _balStore.GeneratedBlockAccessList.AddBalanceChange(address, 0, balance);
            }
            if (!nonce.IsZero)
            {
                _balStore.GeneratedBlockAccessList.AddNonceChange(address, (ulong)nonce);
            }
        }
        _innerWorldState.CreateAccount(address, balance, nonce);
    }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        if (!_innerWorldState.AccountExists(address))
        {
            CreateAccount(address, balance, nonce);
        }
    }

    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        _balStore.AddAccountRead(address);
        return _innerWorldState.TryGetAccount(address, out account);
    }

    public override void Restore(Snapshot snapshot)
    {
        if (_balStore.TracingEnabled)
        {
            _balStore.GeneratedBlockAccessList.Restore(snapshot.BlockAccessListSnapshot);
        }
        _innerWorldState.Restore(snapshot);
    }

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int blockAccessListSnapshot = _balStore.GeneratedBlockAccessList.TakeSnapshot();
        Snapshot snapshot = _innerWorldState.TakeSnapshot(newTransactionStart);
        return new(snapshot.StorageSnapshot, snapshot.StateSnapshot, blockAccessListSnapshot);
    }

    public override bool AccountExists(Address address)
    {
        _balStore.AddAccountRead(address);
        return _innerWorldState.AccountExists(address);
    }

    public override bool IsContract(Address address)
    {
        _balStore.AddAccountRead(address);
        return _innerWorldState.IsContract(address);
    }

    public override bool IsDeadAccount(Address address)
    {
        _balStore.AddAccountRead(address);
        return _innerWorldState.IsDeadAccount(address);
    }

    public override void ClearStorage(Address address)
    {
        _balStore.AddAccountRead(address);
        _innerWorldState.ClearStorage(address);
    }

}
