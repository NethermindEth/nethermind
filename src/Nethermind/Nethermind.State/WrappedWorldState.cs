// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.State;

public class WrappedWorldState(IWorldState innerWorldState) : IWorldState
{
    protected IWorldState _innerWorldState = innerWorldState;
    public bool IsInScope => _innerWorldState.IsInScope;

    public Hash256 StateRoot => _innerWorldState.StateRoot;

    public bool AccountExists(Address address)
        => _innerWorldState.AccountExists(address);

    public virtual void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => _innerWorldState.AddToBalance(address, balanceChange, spec);

    public virtual bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => _innerWorldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);

    public IDisposable BeginScope(BlockHeader? baseBlock)
        => _innerWorldState.BeginScope(baseBlock);

    public void ClearStorage(Address address)
        => _innerWorldState.ClearStorage(address);

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
        => _innerWorldState.Commit(releaseSpec, isGenesis, commitRoots);

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        => _innerWorldState.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    public void CommitTree(long blockNumber)
        => _innerWorldState.CommitTree(blockNumber);

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        => _innerWorldState.CreateAccount(address, balance, nonce);

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        => _innerWorldState.CreateAccountIfNotExists(address, balance, nonce);

    public void DecrementNonce(Address address, UInt256 delta)
        => _innerWorldState.DecrementNonce(address, delta);

    public void DeleteAccount(Address address)
        => _innerWorldState.DeleteAccount(address);

    public virtual ReadOnlySpan<byte> Get(in StorageCell storageCell)
        => _innerWorldState.Get(storageCell);

    public ArrayPoolList<AddressAsKey>? GetAccountChanges()
        => _innerWorldState.GetAccountChanges();

    public ref readonly UInt256 GetBalance(Address address)
        => ref _innerWorldState.GetBalance(address);

    public byte[]? GetCode(Address address)
        => _innerWorldState.GetCode(address);

    public byte[]? GetCode(in ValueHash256 codeHash)
        => _innerWorldState.GetCode(codeHash);

    public ref readonly ValueHash256 GetCodeHash(Address address)
        => ref _innerWorldState.GetCodeHash(address);

    public byte[] GetOriginal(in StorageCell storageCell)
        => _innerWorldState.GetOriginal(storageCell);

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        => _innerWorldState.GetTransientState(storageCell);

    public bool HasStateForBlock(BlockHeader? baseBlock)
        => _innerWorldState.HasStateForBlock(baseBlock);

    public virtual void IncrementNonce(Address address, UInt256 delta)
        => _innerWorldState.IncrementNonce(address, delta);

    public virtual bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => _innerWorldState.InsertCode(address, codeHash, code, spec, isGenesis);

    public bool IsContract(Address address)
        => _innerWorldState.IsContract(address);

    public bool IsDeadAccount(Address address)
        => _innerWorldState.IsDeadAccount(address);

    public void RecalculateStateRoot()
        => _innerWorldState.RecalculateStateRoot();

    public void Reset(bool resetBlockChanges = true)
        => _innerWorldState.Reset(resetBlockChanges);

    public void ResetTransient()
        => _innerWorldState.ResetTransient();

    public void Restore(Snapshot snapshot)
        => _innerWorldState.Restore(snapshot);

    public virtual void Set(in StorageCell storageCell, byte[] newValue)
        => _innerWorldState.Set(storageCell, newValue);

    public void SetNonce(Address address, in UInt256 nonce)
        => _innerWorldState.SetNonce(address, nonce);

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        => _innerWorldState.SetTransientState(storageCell, newValue);

    public virtual void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => _innerWorldState.SubtractFromBalance(address, balanceChange, spec);

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
        => _innerWorldState.TakeSnapshot(newTransactionStart);

    public virtual bool TryGetAccount(Address address, out AccountStruct account)
        => _innerWorldState.TryGetAccount(address, out account);

    public void UpdateStorageRoot(Address address, Hash256 storageRoot)
        => _innerWorldState.UpdateStorageRoot(address, storageRoot);

    public void WarmUp(AccessList? accessList)
        => _innerWorldState.WarmUp(accessList);

    public void WarmUp(Address address)
        => _innerWorldState.WarmUp(address);
}
