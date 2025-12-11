// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

public class WrappedWorldState(IWorldState innerWorldState) : IWorldState
{
    protected IWorldState _innerWorldState = innerWorldState;
    public bool IsInScope => _innerWorldState.IsInScope;

    public Hash256 StateRoot => _innerWorldState.StateRoot;

    public virtual bool AccountExists(Address address, int? blockAccessIndex = null)
        => _innerWorldState.AccountExists(address);

    public virtual void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, int? blockAccessIndex = null)
        => _innerWorldState.AddToBalance(address, balanceChange, spec);

    public virtual void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance, int? blockAccessIndex = null)
        => _innerWorldState.AddToBalance(address, balanceChange, spec, out oldBalance);

    public virtual bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, int? blockAccessIndex = null)
        => _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);

    public virtual bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance, int? blockAccessIndex = null)
        => _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);

    public virtual IDisposable BeginScope(BlockHeader? baseBlock)
        => _innerWorldState.BeginScope(baseBlock);

    public virtual void ClearStorage(Address address)
        => _innerWorldState.ClearStorage(address);

    public virtual void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
        => _innerWorldState.Commit(releaseSpec, isGenesis, commitRoots);

    public virtual void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        => _innerWorldState.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    public virtual void CommitTree(long blockNumber)
        => _innerWorldState.CommitTree(blockNumber);

    public virtual void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        => _innerWorldState.CreateAccount(address, balance, nonce);

    public virtual void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default, int? blockAccessIndex = null)
        => _innerWorldState.CreateAccountIfNotExists(address, balance, nonce);

    // public virtual void CreateEmptyAccountIfDeleted(Address address) =>
    //     _innerWorldState.CreateEmptyAccountIfDeleted(address);

    public virtual void DecrementNonce(Address address, UInt256 delta)
        => _innerWorldState.DecrementNonce(address, delta);

    public virtual void DeleteAccount(Address address, int? blockAccessIndex = null)
        => _innerWorldState.DeleteAccount(address);

    public virtual ReadOnlySpan<byte> Get(in StorageCell storageCell, int? blockAccessIndex = null)
        => _innerWorldState.Get(storageCell);

    public ArrayPoolList<AddressAsKey>? GetAccountChanges()
        => _innerWorldState.GetAccountChanges();

    public virtual UInt256 GetBalance(Address address, int? blockAccessIndex = null)
        => _innerWorldState.GetBalance(address, blockAccessIndex);

    public virtual UInt256 GetNonce(Address address, int? blockAccessIndex = null)
        => _innerWorldState.GetNonce(address, blockAccessIndex);

    public virtual byte[]? GetCode(Address address, int? blockAccessIndex = null)
        => _innerWorldState.GetCode(address);

    public byte[]? GetCode(in ValueHash256 codeHash)
        => _innerWorldState.GetCode(codeHash);

    public virtual ValueHash256 GetCodeHash(Address address, int? blockAccessIndex = null)
        => _innerWorldState.GetCodeHash(address);

    public virtual byte[] GetOriginal(in StorageCell storageCell, int? blockAccessIndex = null)
        => _innerWorldState.GetOriginal(storageCell);

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell, int? blockAccessIndex = null)
        => _innerWorldState.GetTransientState(storageCell);

    public bool HasStateForBlock(BlockHeader? baseBlock)
        => _innerWorldState.HasStateForBlock(baseBlock);

    public virtual void IncrementNonce(Address address, UInt256 delta, int? blockAccessIndex = null)
        => _innerWorldState.IncrementNonce(address, delta);

    public virtual void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce, int? blockAccessIndex = null)
        => _innerWorldState.IncrementNonce(address, delta, out oldNonce);

    public virtual bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false, int? blockAccessIndex = null)
        => _innerWorldState.InsertCode(address, codeHash, code, spec, isGenesis);

    public virtual bool IsContract(Address address, int? blockAccessIndex = null)
        => _innerWorldState.IsContract(address);

    public virtual bool IsDeadAccount(Address address, int? blockAccessIndex = null)
        => _innerWorldState.IsDeadAccount(address);

    public virtual bool IsStorageEmpty(Address address, int? blockAccessIndex = null)
        => _innerWorldState.IsStorageEmpty(address);

    public virtual void RecalculateStateRoot()
        => _innerWorldState.RecalculateStateRoot();

    public virtual void Reset(bool resetBlockChanges = true)
        => _innerWorldState.Reset(resetBlockChanges);

    public void ResetTransient(int? blockAccessIndex = null)
        => _innerWorldState.ResetTransient();

    public virtual void Restore(Snapshot snapshot)
        => _innerWorldState.Restore(snapshot);

    public virtual void Set(in StorageCell storageCell, byte[] newValue, int? blockAccessIndex = null)
        => _innerWorldState.Set(storageCell, newValue);

    public virtual void SetNonce(Address address, in UInt256 nonce)
        => _innerWorldState.SetNonce(address, nonce);

    public void SetTransientState(in StorageCell storageCell, byte[] newValue, int? blockAccessIndex = null)
        => _innerWorldState.SetTransientState(storageCell, newValue);

    public virtual void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, int? blockAccessIndex = null)
        => _innerWorldState.SubtractFromBalance(address, balanceChange, spec);

    public virtual void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        => _innerWorldState.SubtractFromBalance(address, balanceChange, spec, out oldBalance);

    public virtual Snapshot TakeSnapshot(bool newTransactionStart = false)
        => _innerWorldState.TakeSnapshot(newTransactionStart);

    public virtual bool TryGetAccount(Address address, out AccountStruct account, int? blockAccessIndex = null)
        => _innerWorldState.TryGetAccount(address, out account);

    public void WarmUp(AccessList? accessList)
        => _innerWorldState.WarmUp(accessList);

    public void WarmUp(Address address)
        => _innerWorldState.WarmUp(address);
}
