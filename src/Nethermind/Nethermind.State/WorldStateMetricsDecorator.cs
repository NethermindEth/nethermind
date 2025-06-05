// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State.Tracing;
using Nethermind.Trie;

namespace Nethermind.State;

public class WorldStateMetricsDecorator(IWorldState innerState) : IWorldState
{
    public void Restore(Snapshot snapshot) => innerState.Restore(snapshot);

    public bool TryGetAccount(Address address, out AccountStruct account) => innerState.TryGetAccount(address, out account);

    public byte[] GetOriginal(in StorageCell storageCell) => innerState.GetOriginal(in storageCell);

    public ReadOnlySpan<byte> Get(in StorageCell storageCell) => innerState.Get(in storageCell);

    public void Set(in StorageCell storageCell, byte[] newValue) => innerState.Set(in storageCell, newValue);

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell) => innerState.GetTransientState(in storageCell);

    public void SetTransientState(in StorageCell storageCell, byte[] newValue) => innerState.SetTransientState(in storageCell, newValue);

    public void Reset(bool resetBlockChanges = true)
    {
        StateMerkleizationTime = 0d;
        innerState.Reset(resetBlockChanges);
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false) => innerState.TakeSnapshot(newTransactionStart);

    public void WarmUp(AccessList? accessList) => innerState.WarmUp(accessList);

    public void WarmUp(Address address) => innerState.WarmUp(address);

    public void ClearStorage(Address address) => innerState.ClearStorage(address);

    public void RecalculateStateRoot()
    {
        long start = Stopwatch.GetTimestamp();
        innerState.RecalculateStateRoot();
        StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public Hash256 StateRoot
    {
        get => innerState.StateRoot;
        set => innerState.StateRoot = value;
    }

    public double StateMerkleizationTime { get; private set; }

    public void DeleteAccount(Address address) => innerState.DeleteAccount(address);

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default) =>
        innerState.CreateAccount(address, in balance, in nonce);

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default) =>
        innerState.CreateAccountIfNotExists(address, in balance, in nonce);
    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false) =>
        innerState.InsertCode(address, in codeHash, code, spec, isGenesis);

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec) =>
        innerState.AddToBalance(address, in balanceChange, spec);

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec) =>
        innerState.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec);

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec) =>
        innerState.SubtractFromBalance(address, in balanceChange, spec);

    public void UpdateStorageRoot(Address address, Hash256 storageRoot)
        => innerState.UpdateStorageRoot(address, storageRoot);

    public void IncrementNonce(Address address, UInt256 delta) => innerState.IncrementNonce(address, delta);

    public void DecrementNonce(Address address, UInt256 delta) => innerState.DecrementNonce(address, delta);

    public void SetNonce(Address address, in UInt256 nonce) => innerState.SetNonce(address, nonce);

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        long start = Stopwatch.GetTimestamp();
        innerState.Commit(releaseSpec, isGenesis, commitRoots);
        if (commitRoots)
            StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? tracer, bool isGenesis = false, bool commitRoots = true)
    {
        long start = Stopwatch.GetTimestamp();
        innerState.Commit(releaseSpec, tracer, isGenesis, commitRoots);
        if (commitRoots)
            StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public void CommitTree(long blockNumber)
    {
        long start = Stopwatch.GetTimestamp();
        innerState.CommitTree(blockNumber);
        StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public ArrayPoolList<AddressAsKey>? GetAccountChanges() => innerState.GetAccountChanges();

    public void ResetTransient() => innerState.ResetTransient();

    public byte[]? GetCode(Address address) => innerState.GetCode(address);

    public byte[]? GetCode(in ValueHash256 codeHash) => innerState.GetCode(in codeHash);

    public bool IsContract(Address address) => innerState.IsContract(address);

    public void Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx> =>
        innerState.Accept(visitor, stateRoot, visitingOptions);

    public bool AccountExists(Address address) => innerState.AccountExists(address);

    public bool IsDeadAccount(Address address) => innerState.IsDeadAccount(address);

    public bool IsEmptyAccount(Address address) => innerState.IsEmptyAccount(address);

    public bool HasStateForRoot(Hash256 stateRoot) => innerState.HasStateForRoot(stateRoot);

    public ref readonly UInt256 GetBalance(Address account) => ref innerState.GetBalance(account);

    UInt256 IAccountStateProvider.GetBalance(Address address) => innerState.GetBalance(address);

    public ref readonly ValueHash256 GetCodeHash(Address address) => ref innerState.GetCodeHash(address);

    ValueHash256 IAccountStateProvider.GetCodeHash(Address address) => innerState.GetCodeHash(address);
}
