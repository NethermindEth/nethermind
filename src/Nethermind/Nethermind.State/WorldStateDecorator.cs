// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.State;

public abstract class WorldStateDecorator(IWorldState state) : IWorldState
{
    protected readonly IWorldState State = state;

    public virtual Hash256 StateRoot => State.StateRoot;
    public virtual bool IsInScope => State.IsInScope;
    public virtual IWorldStateScopeProvider ScopeProvider => State.ScopeProvider;

    public virtual IDisposable BeginScope(BlockHeader? baseBlock)
        => State.BeginScope(baseBlock);

    public virtual Task HintBal(ReadOnlyBlockAccessList bal)
        => State.HintBal(bal);

    public virtual bool HasStateForBlock(BlockHeader? baseBlock)
        => State.HasStateForBlock(baseBlock);

    public virtual bool TryGetAccount(Address address, out AccountStruct account)
        => State.TryGetAccount(address, out account);

    public virtual ulong GetNonce(Address address)
        => State.GetNonce(address);

    public virtual bool IsStorageEmpty(Address address)
        => State.IsStorageEmpty(address);

    public virtual ref readonly UInt256 GetBalance(Address address)
        => ref State.GetBalance(address);

    public virtual ref readonly ValueHash256 GetCodeHash(Address address)
        => ref State.GetCodeHash(address);

    public virtual byte[]? GetCode(Address address)
        => State.GetCode(address);

    public virtual byte[]? GetCode(in ValueHash256 codeHash)
        => State.GetCode(in codeHash);

    public virtual bool IsContract(Address address)
        => State.IsContract(address);

    public virtual bool AccountExists(Address address)
        => State.AccountExists(address);

    public virtual bool IsDeadAccount(Address address)
        => State.IsDeadAccount(address);

    public virtual ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
        => State.GetOriginal(in storageCell);

    public virtual ReadOnlySpan<byte> Get(in StorageCell storageCell)
        => State.Get(in storageCell);

    public virtual void Set(in StorageCell storageCell, byte[] newValue)
        => State.Set(in storageCell, newValue);

    public virtual ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        => State.GetTransientState(in storageCell);

    public virtual void SetTransientState(in StorageCell storageCell, byte[] newValue)
        => State.SetTransientState(in storageCell, newValue);

    public virtual void Reset(bool resetBlockChanges = true)
        => State.Reset(resetBlockChanges);

    public virtual Snapshot TakeSnapshot(bool newTransactionStart = false)
        => State.TakeSnapshot(newTransactionStart);

    public virtual void Restore(Snapshot snapshot)
        => State.Restore(snapshot);

    public virtual void WarmUp(AccessList? accessList, CancellationToken cancellationToken = default)
        => State.WarmUp(accessList, cancellationToken);

    public virtual void WarmUp(Address address)
        => State.WarmUp(address);

    public virtual void ClearStorage(Address address)
        => State.ClearStorage(address);

    public virtual void RecalculateStateRoot()
        => State.RecalculateStateRoot();

    public virtual void DeleteAccount(Address address)
        => State.DeleteAccount(address);

    public virtual void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
        => State.CreateAccount(address, in balance, in nonce);

    public virtual void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default)
        => State.CreateAccountIfNotExists(address, in balance, in nonce);

    public virtual void CreateEmptyAccountIfDeleted(Address address)
        => State.CreateEmptyAccountIfDeleted(address);

    public virtual bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => State.InsertCode(address, in codeHash, code, spec, isGenesis);

    public virtual void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        => State.AddToBalance(address, in balanceChange, spec, out oldBalance);

    public virtual bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        => State.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);

    public virtual void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        => State.SubtractFromBalance(address, in balanceChange, spec, out oldBalance);

    public virtual void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
        => State.IncrementNonce(address, delta, out oldNonce);

    public virtual void DecrementNonce(Address address, ulong delta)
        => State.DecrementNonce(address, delta);

    public virtual void SetNonce(Address address, in ulong nonce)
        => State.SetNonce(address, in nonce);

    public virtual void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        => State.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    public virtual void CommitTree(ulong blockNumber)
        => State.CommitTree(blockNumber);

    public virtual ArrayPoolList<AddressAsKey>? GetAccountChanges()
        => State.GetAccountChanges();

    public virtual void ResetTransient()
        => State.ResetTransient();

    public virtual void AddAccountRead(Address address)
        => State.AddAccountRead(address);

    public virtual void RecordAccountAccess(Address address)
        => State.RecordAccountAccess(address);

    public virtual void RecordBytecodeAccess(Address address)
        => State.RecordBytecodeAccess(address);

    public virtual IDisposable? BeginSystemAccountReadSuppression()
        => State.BeginSystemAccountReadSuppression();
}
