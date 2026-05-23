// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// <see cref="IWorldState"/> decorator installed on the main-processing pipeline that routes every
/// call to either the active per-block recorder (when capture is in progress) or straight through
/// to the inner world state.
/// </summary>
/// <remarks>
/// Holds no recording state itself — the recorder ([[witness-generating-world-state]]) owns the
/// recording dictionaries for the lifetime of one <c>ProcessOne</c> call. The block-processor
/// decorator ([[witness-capturing-block-processor]]) drives <see cref="TryActivate"/> /
/// <see cref="Deactivate"/>, and the rendezvous ([[witness-rendezvous]]) owns the cross-thread
/// completion.
/// </remarks>
public sealed class WitnessCapturingWorldStateProxy(IWorldState inner) : IWorldState
{
    private WitnessGeneratingWorldState? _active;

    /// <summary>The undecorated inner world state. Used by the block-processor decorator to anchor a fresh recorder.</summary>
    internal IWorldState InnerState => inner;

    /// <summary>True iff a recorder is currently installed (i.e. capture is in progress).</summary>
    internal bool IsActive => _active is not null;

    /// <summary>
    /// Atomically install <paramref name="recorder"/> as the active routing target. Returns <c>false</c>
    /// if another recorder is already active (nested or concurrent capture is not supported).
    /// </summary>
    internal bool TryActivate(WitnessGeneratingWorldState recorder)
        => Interlocked.CompareExchange(ref _active, recorder, null) is null;

    /// <summary>Remove <paramref name="recorder"/> if it is the active one; no-op otherwise.</summary>
    internal void Deactivate(WitnessGeneratingWorldState recorder)
        => Interlocked.CompareExchange(ref _active, null, recorder);

    private IWorldState Current => _active ?? inner;

    public bool HasStateForBlock(BlockHeader? baseBlock) => Current.HasStateForBlock(baseBlock);
    public void Restore(Snapshot snapshot) => Current.Restore(snapshot);
    public Hash256 StateRoot => Current.StateRoot;
    public bool IsInScope => Current.IsInScope;
    public IWorldStateScopeProvider ScopeProvider => Current.ScopeProvider;
    public IDisposable BeginScope(BlockHeader? baseBlock) => Current.BeginScope(baseBlock);

    public bool TryGetAccount(Address address, out AccountStruct account) => Current.TryGetAccount(address, out account);
    public UInt256 GetNonce(Address address) => Current.GetNonce(address);
    public bool IsStorageEmpty(Address address) => Current.IsStorageEmpty(address);
    public bool HasCode(Address address) => Current.HasCode(address);
    public bool IsNonZeroAccount(Address address, out bool accountExists) => Current.IsNonZeroAccount(address, out accountExists);
    public bool IsDelegatedCode(Address address) => Current.IsDelegatedCode(address);
    public bool IsDelegatedCode(in ValueHash256 codeHash) => Current.IsDelegatedCode(in codeHash);
    public byte[]? GetCode(Address address) => Current.GetCode(address);
    public byte[]? GetCode(in ValueHash256 codeHash) => Current.GetCode(in codeHash);
    public bool IsContract(Address address) => Current.IsContract(address);
    public bool AccountExists(Address address) => Current.AccountExists(address);
    public bool IsDeadAccount(Address address) => Current.IsDeadAccount(address);
    public ref readonly UInt256 GetBalance(Address address) => ref Current.GetBalance(address);
    public ref readonly ValueHash256 GetCodeHash(Address address) => ref Current.GetCodeHash(address);

    public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell) => Current.GetOriginal(in storageCell);
    public ReadOnlySpan<byte> Get(in StorageCell storageCell) => Current.Get(in storageCell);
    public void Set(in StorageCell storageCell, byte[] newValue) => Current.Set(in storageCell, newValue);

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell) => Current.GetTransientState(in storageCell);
    public void SetTransientState(in StorageCell storageCell, byte[] newValue) => Current.SetTransientState(in storageCell, newValue);

    public void Reset(bool resetBlockChanges = true) => Current.Reset(resetBlockChanges);
    public Snapshot TakeSnapshot(bool newTransactionStart = false) => Current.TakeSnapshot(newTransactionStart);

    public void WarmUp(AccessList? accessList) => Current.WarmUp(accessList);
    public void WarmUp(Address address) => Current.WarmUp(address);

    public void ClearStorage(Address address) => Current.ClearStorage(address);
    public void RecalculateStateRoot() => Current.RecalculateStateRoot();

    public void DeleteAccount(Address address) => Current.DeleteAccount(address);
    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default) => Current.CreateAccount(address, in balance, in nonce);
    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default) => Current.CreateAccountIfNotExists(address, in balance, in nonce);

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false) =>
        Current.InsertCode(address, in codeHash, code, spec, isGenesis);

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) =>
        Current.AddToBalance(address, in balanceChange, spec, out oldBalance);

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) =>
        Current.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) =>
        Current.SubtractFromBalance(address, in balanceChange, spec, out oldBalance);

    public void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce) => Current.IncrementNonce(address, delta, out oldNonce);
    public void DecrementNonce(Address address, UInt256 delta) => Current.DecrementNonce(address, delta);
    public void SetNonce(Address address, in UInt256 nonce) => Current.SetNonce(address, in nonce);

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true) =>
        Current.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    public void CommitTree(long blockNumber) => Current.CommitTree(blockNumber);
    public ArrayPoolList<AddressAsKey>? GetAccountChanges() => Current.GetAccountChanges();
    public void ResetTransient() => Current.ResetTransient();

    public void CreateEmptyAccountIfDeleted(Address address) => Current.CreateEmptyAccountIfDeleted(address);
    public void AddAccountRead(Address address) => Current.AddAccountRead(address);
    public IDisposable? BeginSystemAccountReadSuppression() => Current.BeginSystemAccountReadSuppression();

    internal void RecordSystemContractAccess(Address address, UInt256 slotIndex, byte[]? code)
        => _active?.RecordSystemContractAccess(address, slotIndex, code);

    internal void RecordSystemContractAccountAccess(Address address, byte[]? code)
        => _active?.RecordSystemContractAccountAccess(address, code);
}
