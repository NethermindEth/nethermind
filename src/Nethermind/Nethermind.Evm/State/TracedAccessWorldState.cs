// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

public class TracedAccessWorldState(IWorldState innerWorldState) : WrappedWorldState(innerWorldState), IPreBlockCaches
{
    public bool Enabled { get; set; } = false;
    public BlockAccessList BlockAccessList = new();

    public PreBlockCaches Caches => (_innerWorldState as IPreBlockCaches).Caches;

    public bool IsWarmWorldState => (_innerWorldState as IPreBlockCaches).IsWarmWorldState;

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => AddToBalance(address, balanceChange, spec, out _);

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        _innerWorldState.AddToBalance(address, balanceChange, spec, out oldBalance);

        if (Enabled)
        {
            UInt256 newBalance = oldBalance + balanceChange;
            BlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out _);

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        bool res = _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);

        if (Enabled)
        {
            UInt256 newBalance = oldBalance + balanceChange;
            BlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        }

        return res;
    }

    public override IDisposable BeginScope(BlockHeader? baseBlock)
    {
        BlockAccessList = new();
        return _innerWorldState.BeginScope(baseBlock);
    }

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        if (Enabled)
        {
            BlockAccessList.AddStorageRead(storageCell);
        }
        return _innerWorldState.Get(storageCell);
    }

    public override void IncrementNonce(Address address, UInt256 delta)
        => IncrementNonce(address, delta, out _);

    public override void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        _innerWorldState.IncrementNonce(address, delta, out oldNonce);

        if (Enabled)
        {
            BlockAccessList.AddNonceChange(address, (ulong)(oldNonce + delta));
        }
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        if (Enabled)
        {
            byte[] oldCode = _innerWorldState.GetCode(address) ?? [];
            BlockAccessList.AddCodeChange(address, oldCode, code.ToArray());
        }
        return _innerWorldState.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        if (Enabled)
        {
            ReadOnlySpan<byte> oldValue = _innerWorldState.Get(storageCell);
            BlockAccessList.AddStorageChange(storageCell, [.. oldValue], newValue);
        }
        _innerWorldState.Set(storageCell, newValue);
    }

    public override UInt256 GetBalance(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.GetBalance(address);
    }

    public override ValueHash256 GetCodeHash(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.GetCodeHash(address);
    }

    public override byte[]? GetCode(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.GetCode(address);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        if (Enabled)
        {
            UInt256 before = _innerWorldState.GetBalance(address);
            UInt256 after = before - balanceChange;
            BlockAccessList.AddBalanceChange(address, before, after);
        }
        _innerWorldState.SubtractFromBalance(address, balanceChange, spec);
    }

    public override void DeleteAccount(Address address)
    {
        if (Enabled)
        {
            BlockAccessList.DeleteAccount(address);
        }
        _innerWorldState.DeleteAccount(address);
    }

    public override void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        if (Enabled)
        {
            BlockAccessList.AddAccountRead(address);
            if (balance != 0)
            {
                BlockAccessList.AddBalanceChange(address, 0, balance);
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
        AddAccountRead(address);
        return _innerWorldState.TryGetAccount(address, out account);
    }

    public void AddAccountRead(Address address)
    {
        if (Enabled)
        {
            BlockAccessList.AddAccountRead(address);
        }
    }

    public override void Restore(Snapshot snapshot)
    {
        if (Enabled)
        {
            BlockAccessList.Restore(snapshot.BlockAccessListSnapshot);
        }
        _innerWorldState.Restore(snapshot);
    }

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int blockAccessListSnapshot = BlockAccessList.TakeSnapshot();
        Snapshot snapshot = _innerWorldState.TakeSnapshot(newTransactionStart);
        return new(snapshot.StorageSnapshot, snapshot.StateSnapshot, blockAccessListSnapshot);
    }

    public override bool AccountExists(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.AccountExists(address);
    }

    public override bool IsContract(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.AccountExists(address);
    }

    public override bool IsDeadAccount(Address address)
    {
        AddAccountRead(address);
        return _innerWorldState.AccountExists(address);
    }

    public override void ClearStorage(Address address)
    {
        // todo: change all storage slots to nothing
        // consensus issue?
        AddAccountRead(address);
        _innerWorldState.ClearStorage(address);
    }
}
