// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

public class TracedAccessWorldState(IWorldState innerWorldState) : WrappedWorldState(innerWorldState), IPreBlockCaches
{
    public BlockAccessList BlockAccessList = new();

    public PreBlockCaches Caches => (_innerWorldState as IPreBlockCaches).Caches;

    // public bool IsInScope => innerWorldState.IsInScope;

    // public Hash256 StateRoot => innerWorldState.StateRoot;

    // public bool AccountExists(Address address) => innerWorldState.AccountExists(address);

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => AddToBalance(address, balanceChange, spec, out _);
    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        _innerWorldState.AddToBalance(address, balanceChange, spec, out oldBalance);
        UInt256 newBalance = oldBalance + balanceChange;
        BlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out _);
    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        bool res = _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);
        UInt256 newBalance = oldBalance + balanceChange;
        BlockAccessList.AddBalanceChange(address, oldBalance, newBalance);
        return res;
    }

    public override IDisposable BeginScope(BlockHeader? baseBlock)
    {
        BlockAccessList = new();
        return _innerWorldState.BeginScope(baseBlock);
    }

    // public void ClearStorage(Address address)
    // => innerWorldState.ClearStorage(address);

    // public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    // => innerWorldState.Commit(releaseSpec, isGenesis, commitRoots);

    // public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
    // => innerWorldState.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    // public void CommitTree(long blockNumber)
    // => innerWorldState.CommitTree(blockNumber);

    // public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    // =>
    //     innerWorldState.CreateAccount(address, balance, nonce);

    // public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default) =>
    //     innerWorldState.CreateAccountIfNotExists(address, balance, nonce);

    // public void DecrementNonce(Address address, UInt256 delta) =>
    //     innerWorldState.DecrementNonce(address, delta);

    // public void DeleteAccount(Address address) =>
    //     innerWorldState.DeleteAccount(address);

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        BlockAccessList.AddStorageRead(storageCell);
        return _innerWorldState.Get(storageCell);
    }

    // public ArrayPoolList<AddressAsKey>? GetAccountChanges() =>
    //     innerWorldState.GetAccountChanges();

    // public ref readonly UInt256 GetBalance(Address address) =>
    //     ref innerWorldState.GetBalance(address);

    // public byte[]? GetCode(Address address) =>
    //     innerWorldState.GetCode(address);

    // public byte[]? GetCode(in ValueHash256 codeHash) =>
    //     innerWorldState.GetCode(codeHash);

    // public ref readonly ValueHash256 GetCodeHash(Address address) =>
    //     ref innerWorldState.GetCodeHash(address);

    // public byte[] GetOriginal(in StorageCell storageCell) =>
    //     innerWorldState.GetOriginal(storageCell);

    // public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell) =>
    //     innerWorldState.GetTransientState(storageCell);

    // public bool HasStateForBlock(BlockHeader? baseBlock) =>
    //     innerWorldState.HasStateForBlock(baseBlock);

    public override void IncrementNonce(Address address, UInt256 delta)
        => IncrementNonce(address, delta, out _);

    public override void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce)
    {
        _innerWorldState.IncrementNonce(address, delta, out oldNonce);
        BlockAccessList.AddNonceChange(address, (ulong)(oldNonce + delta));
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        BlockAccessList.AddCodeChange(address, code.ToArray());
        return _innerWorldState.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    // public bool IsContract(Address address) =>
    //     innerWorldState.IsContract(address);

    // public bool IsDeadAccount(Address address) =>
    //     innerWorldState.IsDeadAccount(address);

    // public void RecalculateStateRoot() =>
    //     innerWorldState.RecalculateStateRoot();

    // public void Reset(bool resetBlockChanges = true) =>
    //     innerWorldState.Reset(resetBlockChanges);

    // public void ResetTransient() =>
    //     innerWorldState.ResetTransient();

    // public void Restore(Snapshot snapshot) =>
    //     innerWorldState.Restore(snapshot);

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        ReadOnlySpan<byte> oldValue = _innerWorldState.Get(storageCell);
        BlockAccessList.AddStorageChange(storageCell, [.. oldValue], newValue);
        _innerWorldState.Set(storageCell, newValue);
    }

        // public override void SetNonce(Address address, in UInt256 nonce) =>
        //     _innerWorldState.SetNonce(address, nonce);

        // public void SetTransientState(in StorageCell storageCell, byte[] newValue) =>
        //     innerWorldState.SetTransientState(storageCell, newValue);

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        UInt256 before = _innerWorldState.GetBalance(address);
        UInt256 after = before - balanceChange;
        _innerWorldState.SubtractFromBalance(address, balanceChange, spec);
        BlockAccessList.AddBalanceChange(address, before, after);
    }

    // public Snapshot TakeSnapshot(bool newTransactionStart = false) =>
    //     innerWorldState.TakeSnapshot(newTransactionStart);

    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        BlockAccessList.AddAccountRead(address);
        return _innerWorldState.TryGetAccount(address, out account);
    }
    // public void UpdateStorageRoot(Address address, Hash256 storageRoot) =>
    //     innerWorldState.UpdateStorageRoot(address, storageRoot);

    // public void WarmUp(AccessList? accessList) =>
    //     innerWorldState.WarmUp(accessList);

    // public void WarmUp(Address address) =>
    //     innerWorldState.WarmUp(address);
}
