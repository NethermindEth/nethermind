// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.State;

/// <summary>
/// A read-side world-state shim used by parallel-validation workers. Each EVM read goes
/// through the suggested BAL first; if the BAL doesn't carry an entry for that
/// (address, slot) at the current block-access index, we fall through to a per-worker
/// <see cref="_parentReader"/> snapshot of the pre-block state. Writes are no-ops — the
/// generated BAL journal owned by <see cref="TracedAccessWorldState"/> records every change.
/// </summary>
/// <remarks>
/// BAL completeness is still enforced: if an account is not declared in the suggested BAL
/// at all, reads throw <see cref="InvalidBlockLevelAccessListException"/>. This mirrors what
/// the sequential path's <c>ValidateBlockAccessList</c> would surface after the tx executed,
/// so both paths produce the same <see cref="InvalidBlockException"/> for an under-specified
/// BAL.
/// </remarks>
public class BlockAccessListBasedWorldState(IWorldState innerWorldState, ILogManager logManager) : IWorldState
{
    protected IWorldState _innerWorldState = innerWorldState;
    private ReadOnlyBlockAccessList? _suggestedBlockAccessList;
    private BlockHeader? _suggestedBlockHeader;
    private IWorldState? _parentReader;
    private uint _blockAccessIndex = 0;
    private readonly TransientStorageProvider _transientStorageProvider = new(logManager);
    private UInt256 _scratchBalance;
    private ValueHash256 _scratchCodeHash;
    // Scratch buffer for ReadOnlySlotChanges.Get on the SLOAD hot path. Per-instance and
    // per-parallel-worker (each worker holds its own BlockAccessListBasedWorldState), so the
    // single buffer is safe — only one Get() runs on this instance at a time, and the returned
    // span is consumed before another Get() can overwrite the buffer.
    private readonly byte[] _scratchStorage = new byte[32];

    public void SetBlockAccessIndex(uint index) => _blockAccessIndex = index;

    public bool IsInScope => _innerWorldState.IsInScope;
    public IWorldStateScopeProvider ScopeProvider => _innerWorldState.ScopeProvider;
    public Hash256 StateRoot => _innerWorldState.StateRoot;

    public void Setup(Block suggestedBlock)
    {
        _suggestedBlockAccessList = suggestedBlock.BlockAccessList;
        _suggestedBlockHeader = suggestedBlock.Header;
        _transientStorageProvider.Reset();
    }

    /// <summary>Attaches a per-worker snapshot of the pre-block state. The reader is read-only
    /// and is the fall-through source for any (address, slot) the BAL doesn't cover at the
    /// current block-access index. Must be cleared via <see cref="ClearParentReader"/> when the
    /// processor is returned to the pool, so the snapshot scope is disposed promptly.</summary>
    public void SetParentReader(IWorldState parentReader)
    {
        if (_parentReader is not null && !ReferenceEquals(_parentReader, parentReader))
        {
            ThrowParentReaderAlreadyAttached();
        }

        _parentReader = parentReader;
    }

    public void ClearParentReader()
    {
        _parentReader = null;
        _suggestedBlockAccessList = null;
        _suggestedBlockHeader = null;
    }

    public bool HasStateForBlock(BlockHeader? baseBlock)
        => _innerWorldState.HasStateForBlock(baseBlock);

    public void WarmUp(AccessList? accessList)
        => _innerWorldState.WarmUp(accessList);

    public void WarmUp(Address address)
        => _innerWorldState.WarmUp(address);

    public class InvalidBlockLevelAccessListException(BlockHeader block, string message) : InvalidBlockException(block, "InvalidBlockLevelAccessList: " + message);

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) => oldBalance = GetBalance(address);

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        oldBalance = GetBalance(address);
        return !AccountExists(address);
    }

    public IDisposable BeginScope(BlockHeader? baseBlock)
        => _innerWorldState.BeginScope(baseBlock);

    public ReadOnlySpan<byte> Get(in StorageCell storageCell) => GetAtCurrentIndex(in storageCell);

    // Same lookup as Get: the BAL slot view is the value strictly before _blockAccessIndex,
    // which is the start-of-tx (= "original") value. Intra-tx writes never reach this state
    // — they go through the per-tx journal owned by TracedAccessWorldState — so EIP-2200's
    // "original" and "current" both resolve to the same BAL slot here.
    public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell) => GetAtCurrentIndex(in storageCell);

    private ReadOnlySpan<byte> GetAtCurrentIndex(in StorageCell storageCell)
    {
        ReadOnlyAccountChanges accountChanges = ResolveAccountChanges(storageCell.Address);
        if (accountChanges.TryGetSlotChanges(storageCell.Index, out ReadOnlySlotChanges? slotChanges))
        {
            if (slotChanges.TryGetLastBefore(_blockAccessIndex, _scratchStorage, out ReadOnlySpan<byte> span))
            {
                return span;
            }

            return GetParentReader().Get(in storageCell);
        }

        if (accountChanges.IsStorageRead(storageCell.Index))
        {
            return GetParentReader().Get(in storageCell);
        }

        ThrowMissingStorage(storageCell);
        return default;
    }

    public void IncrementNonce(Address address, UInt256 delta, out UInt256 oldNonce) => oldNonce = GetNonce(address);

    public void SetNonce(Address address, in UInt256 nonce) { }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => true;

    public void Set(in StorageCell storageCell, byte[] newValue) { }

    public ref readonly UInt256 GetBalance(Address address)
    {
        ReadOnlyAccountChanges accountChanges = ResolveAccountChanges(address);
        _scratchBalance = accountChanges.GetBalance(_blockAccessIndex) ?? GetParentReader().GetBalance(address);
        return ref _scratchBalance;
    }

    public UInt256 GetNonce(Address address)
    {
        ReadOnlyAccountChanges accountChanges = ResolveAccountChanges(address);
        return accountChanges.GetNonce(_blockAccessIndex) ?? GetParentReader().GetNonce(address);
    }

    public ref readonly ValueHash256 GetCodeHash(Address address)
    {
        ReadOnlyAccountChanges accountChanges = ResolveAccountChanges(address);
        if (accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange))
        {
            _scratchCodeHash = codeChange.CodeHash;
            return ref _scratchCodeHash;
        }
        _scratchCodeHash = GetParentReader().GetCodeHash(address);
        return ref _scratchCodeHash;
    }

    public byte[]? GetCode(Address address)
    {
        ReadOnlyAccountChanges accountChanges = ResolveAccountChanges(address);
        return accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange)
            ? codeChange.Code
            : GetParentReader().GetCode(address);
    }

    public byte[]? GetCode(in ValueHash256 _)
        => null;

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) => oldBalance = GetBalance(address);

    public void DeleteAccount(Address address) { }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        ReadOnlyAccountChanges accountChanges = ResolveAccountChanges(address);
        IWorldState parentReader = GetParentReader();

        bool exists = parentReader.TryGetAccount(address, out account);
        UInt256 nonce = exists ? account.Nonce : UInt256.Zero;
        UInt256 balance = exists ? account.Balance : UInt256.Zero;
        ValueHash256 storageRoot = exists ? account.StorageRoot : Keccak.EmptyTreeHash.ValueHash256;
        ValueHash256 codeHash = exists ? account.CodeHash : Keccak.OfAnEmptyString.ValueHash256;

        bool hasPriorChange = false;
        if (accountChanges.TryGetLastNonceChangeBefore(_blockAccessIndex, out NonceChange nonceChange))
        {
            nonce = nonceChange.Value;
            hasPriorChange = true;
        }

        if (accountChanges.TryGetLastBalanceChangeBefore(_blockAccessIndex, out BalanceChange balanceChange))
        {
            balance = balanceChange.Value;
            hasPriorChange = true;
        }

        if (accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange))
        {
            codeHash = codeChange.CodeHash;
            hasPriorChange |= codeChange.Code.Length != 0;
        }

        if (!exists && !hasPriorChange)
        {
            account = AccountStruct.TotallyEmpty;
            return false;
        }

        account = new(nonce, balance, storageRoot, codeHash);
        return true;
    }

    // BAL-backed account/storage mutations are restored by the generated BAL journal;
    // the world-state snapshot only owns transient storage for this wrapper.
    public void Restore(Snapshot snapshot)
        => _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
        return new Snapshot(new Snapshot.Storage(Snapshot.EmptyPosition, transientSnapshot), Snapshot.EmptyPosition);
    }

    public bool AccountExists(Address address)
    {
        ReadOnlyAccountChanges accountChanges = ResolveAccountChanges(address);

        if (GetParentReader().AccountExists(address))
        {
            return true;
        }

        if (accountChanges.TryGetLastNonceChangeBefore(_blockAccessIndex, out _))
        {
            return true;
        }

        if (accountChanges.TryGetLastBalanceChangeBefore(_blockAccessIndex, out _))
        {
            return true;
        }

        // EIP-7702 / EIP-7928: a code-only modification (e.g. SetCode) at a prior tx also
        // implies existence at this index, but only when the resulting code is non-empty.
        return accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange)
            && codeChange.Code.Length != 0;
    }

    public bool IsContract(Address address)
        => GetCodeHash(address) != Keccak.OfAnEmptyString;

    public bool IsStorageEmpty(Address address)
    {
        // Storage emptiness is a property of the pre-block state; the BAL only carries
        // changes within the block, never a "before" sentinel. Delegate straight to the
        // parent reader, which reads from the actual state trie.
        _ = ResolveAccountChanges(address);
        return GetParentReader().IsStorageEmpty(address);
    }

    public bool IsDeadAccount(Address address)
        => !AccountExists(address) ||
                (GetBalance(address) == 0 &&
                GetNonce(address) == 0 &&
                GetCodeHash(address) == Keccak.OfAnEmptyString);

    public void ClearStorage(Address address) { }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true) { }

    public void CommitTree(long blockNumber)
        => _innerWorldState.CommitTree(blockNumber);

    public void DecrementNonce(Address address, UInt256 delta) { }

    public void RecalculateStateRoot() { }

    public void Reset(bool resetBlockChanges = true) { }

    public ArrayPoolList<AddressAsKey> GetAccountChanges()
    {
        CheckInitialized();

        ArrayPoolList<AddressAsKey> result = new(_suggestedBlockAccessList.AccountChanges.Count);
        foreach (ReadOnlyAccountChanges accountChanges in _suggestedBlockAccessList.AccountChanges)
        {
            if (accountChanges.HasStateChanges)
            {
                result.Add(new AddressAsKey(accountChanges.Address));
            }
        }
        return result;
    }

    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        => _transientStorageProvider.Get(in storageCell);

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        => _transientStorageProvider.Set(in storageCell, newValue);

    public void ResetTransient()
        => _transientStorageProvider.Reset();

    public void CreateEmptyAccountIfDeleted(Address address)
        => _innerWorldState.CreateEmptyAccountIfDeleted(address);

    [MemberNotNull(nameof(_suggestedBlockAccessList), nameof(_suggestedBlockHeader))]
    private void CheckInitialized()
    {
        if (_suggestedBlockAccessList is null) ThrowNotInitialized(nameof(_suggestedBlockAccessList));
        if (_suggestedBlockHeader is null) ThrowNotInitialized(nameof(_suggestedBlockHeader));
    }

    private IWorldState GetParentReader()
    {
        if (_parentReader is null) ThrowNotInitialized(nameof(_parentReader));
        return _parentReader;
    }

    private ReadOnlyAccountChanges ResolveAccountChanges(Address address)
    {
        CheckInitialized();
        ReadOnlyAccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
        if (accountChanges is null) ThrowMissingAccount(address);
        return accountChanges;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowParentReaderAlreadyAttached()
        => throw new InvalidOperationException($"{nameof(_parentReader)} is already attached.");

    [DoesNotReturn, StackTraceHidden]
    private void ThrowMissingAccount(Address address)
        => throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader!,
            $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");

    [DoesNotReturn, StackTraceHidden]
    private void ThrowMissingStorage(in StorageCell storageCell)
        => throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader!,
            $"Storage access for {storageCell.Address} not in block access list at index {_blockAccessIndex}.");
}
