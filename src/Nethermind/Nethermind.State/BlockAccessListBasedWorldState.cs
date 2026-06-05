// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.State;

public class BlockAccessListBasedWorldState(IWorldState innerWorldState, ILogManager logManager, PreBlockCaches? preBlockCaches = null) : IWorldState
{
    protected IWorldState _innerWorldState = innerWorldState;
    private ReadOnlyBlockAccessList? _suggestedBlockAccessList;
    private BlockHeader? _suggestedBlockHeader;
    private IWorldState? _parentReader;
    private Dictionary<ValueHash256, (uint Index, byte[] Code)>? _codeChangesByHash;
    private uint _blockAccessIndex = 0;
    // Verify-only read coverage (replaces generated read materialization). A fresh instance is rented
    // per slice (per tx) when PreBlockCaches.ReadCoverageEnabled, marked per declared read; the
    // validator OR-reduces every slice's coverage at block end. Per-slice, so no block tag is needed:
    // only one block runs at a time and the worker's state is cleared at slice return.
    private BalReadCoverage? _readCoverage;
    // Single per-account context cache shared by every read: same-account streams (the bloat loop
    // walks one account, ascending slots) reuse the resolved BAL account changes and the coverage
    // plan index/cursor instead of re-probing the BAL account dictionary on each read. Keyed by
    // _contextAccount; invalidated when the queried address changes (or per slice in Setup).
    private Address? _contextAccount;
    private ReadOnlyAccountChanges? _contextChanges;
    private int _coverageAccountIndex;
    private bool _coverageHasAccount;
    private bool _coveragePlanResolved;       // plan index lazily resolved for _contextAccount
    private int _coverageCursor;              // ascending-read cursor within _contextAccount
    private EvmWord _readScratch;
    private EvmWord _originalScratch;
    private UInt256 _scratchBalance;
    private ValueHash256 _scratchCodeHash;
    private readonly TransientStorageProvider _transientStorageProvider = new(logManager);

    // _codeChangesByHash is monotonic across the block (a code blob deployed at any tx is
    // queryable at any later tx), so it is built once per block in Setup and not rebuilt here.
    public void SetBlockAccessIndex(uint index) => _blockAccessIndex = index;

    public bool IsInScope => _innerWorldState.IsInScope;
    public IWorldStateScopeProvider ScopeProvider => _innerWorldState.ScopeProvider;
    public Hash256 StateRoot => _innerWorldState.StateRoot;

    public void Setup(Block suggestedBlock)
    {
        _suggestedBlockAccessList = suggestedBlock.BlockAccessList;
        _suggestedBlockHeader = suggestedBlock.Header;
        _codeChangesByHash = BuildCodeChangesByHash();
        _transientStorageProvider.Reset();
        ResetContextCache();
        SetupReadCoverage();
    }

    /// <summary>Whether this worker is marking verify-only read coverage for the current block.</summary>
    public bool ReadCoverageActive => _readCoverage is not null;

    /// <inheritdoc/>
    /// <remarks>The block's declared-read ordinal plan, present whenever the prefetch/coverage plan was built.</remarks>
    public BalReadStoragePlan? GetActiveDeclaredReadPlan() => preBlockCaches?.StorageReadPlan;

    /// <summary>
    /// Distinct chargeable (non-system) declared reads this worker marked for the current slice. Read
    /// by the pool at slice return so the validator can accumulate the chargeable budget per slice.
    /// </summary>
    public long CurrentSliceChargeableReads => _readCoverage?.ChargeableCount ?? 0;

    // Setup runs per tx (per slice): rent a fresh coverage instance enqueued for the block-end reduce;
    // the worker forgets it at slice return.
    private void SetupReadCoverage()
        => _readCoverage = preBlockCaches is { ReadCoverageEnabled: true } caches ? caches.RentReadCoverage() : null;

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
        _codeChangesByHash = null;
        // The slice's coverage is owned by the block-end reduce queue now; drop our reference so the
        // next slice rents a fresh one.
        _readCoverage = null;
        ResetContextCache();
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

    public Task HintBal(ReadOnlyBlockAccessList bal) => _innerWorldState.HintBal(bal);

    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(storageCell.Address);

        if (TryGetDeclaredSlotChanges(accountChanges, storageCell.Index, out ReadOnlySlotChanges? slotChanges))
        {
            if (slotChanges is not null && slotChanges.TryGetLastBefore(_blockAccessIndex, out StorageChange storageChange))
            {
                // Copy the BE bytes into per-instance scratch; span valid until the next Get on this instance.
                _readScratch = storageChange.Value;
                return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref _readScratch), 32)
                    .WithoutLeadingZeros();
            }

            // Pure declared read (slotChanges is null): a slot the BAL proves is read-only this block,
            // so original == current == pre-state. Mark verify-only read coverage, then serve it from
            // the prefetched ordinal destination or the parent's registry-bypassing read; both are
            // byte-identical to parentReader.Get.
            if (slotChanges is null)
            {
                MarkDeclaredReadCoverage(in storageCell);
                if (TryReadDeclaredPureRead(in storageCell, out byte[]? value))
                {
                    return value;
                }
            }

            return parentReader.Get(storageCell);
        }

        ThrowMissingStorage(storageCell);
        return default;
    }

    // Marks a declared storage read in the per-worker coverage, replacing generated read-set
    // materialization. Folds the ordinal lookup onto an ascending-read cursor (O(1) for sorted streams).
    // GetOriginal does not mark: it is only reached after Get for the same slot (SSTORE), already marked.
    private void MarkDeclaredReadCoverage(in StorageCell storageCell)
    {
        BalReadCoverage? coverage = _readCoverage;
        if (coverage is null) return;

        // ResolveContext already keyed _contextAccount to this address (Get resolved it first), so the
        // cursor is valid for it; resolve the plan account index lazily, once per account.
        BalReadStoragePlan plan = preBlockCaches!.StorageReadPlan!;
        if (!_coveragePlanResolved)
        {
            _coverageHasAccount = plan.TryGetAccountIndex(storageCell.Address, out _coverageAccountIndex);
            _coveragePlanResolved = true;
        }

        if (_coverageHasAccount &&
            plan.TryGetReadLocalIndex(_coverageAccountIndex, in storageCell.Index, ref _coverageCursor, out int localIndex))
        {
            int ordinal = plan.GlobalReadOrdinal(_coverageAccountIndex, localIndex);
            coverage.MarkRead(ordinal, chargeable: !IsSystemContract(storageCell.Address));
        }
    }

    // System-contract reads are structurally compared but excluded from the chargeable read budget.
    private static bool IsSystemContract(Address address)
        => address == Eip7002Constants.WithdrawalRequestPredeployAddress
        || address == Eip7251Constants.ConsolidationRequestPredeployAddress;

    /// <remarks>
    /// For a BAL-declared read the value never changes in-block, so the same lookup answers both
    /// <see cref="Get"/> and <see cref="GetOriginal"/> and never needs the parent's change registry -
    /// which is what lets <see cref="GetOriginal"/> avoid <c>parentReader.GetOriginal</c> (it would
    /// throw without a prior registered read).
    /// <para>
    /// Gated on the ordinal destination existing (large blocks): there the destination serves repeats
    /// in O(1), and a destination miss is read once through the parent's journal-bypassing read and
    /// then cached by ordinal so later repeats also hit O(1). Without a destination (small blocks)
    /// this returns false so the caller uses the normal registered read, whose upper cache is faster
    /// for repeated same-slot reads.
    /// </para>
    /// </remarks>
    private bool TryReadDeclaredPureRead(in StorageCell storageCell, out byte[]? value)
    {
        if (preBlockCaches?.StorageValueDestination is not { } destination
            || preBlockCaches.StorageReadPlan is not { } readPlan
            || !readPlan.TryGetGlobalReadOrdinal(storageCell.Address, in storageCell.Index, out int ordinal))
        {
            value = null;
            return false;
        }

        if (destination.TryGet(ordinal, out value))
        {
            return true; // prefetched or previously cached read-through; null/empty => zero slot
        }

        // Destination miss: read once through the parent without journaling, then cache by ordinal so
        // repeats of this not-yet-prefetched declared slot hit O(1) instead of re-probing.
        if (_parentReader is not null && _parentReader.TryGetPureReadStorage(in storageCell, out value))
        {
            destination.Set(ordinal, value);
            return true;
        }

        value = null;
        return false;
    }

    public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(storageCell.Address);

        if (TryGetDeclaredSlotChanges(accountChanges, storageCell.Index, out ReadOnlySlotChanges? slotChanges))
        {
            if (slotChanges is not null && slotChanges.TryGetLastBefore(_blockAccessIndex, out StorageChange storageChange))
            {
                _originalScratch = storageChange.Value;
                return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref _originalScratch), 32)
                    .WithoutLeadingZeros();
            }

            // Declared read: original == pre-state, so use the same pure read as Get rather than
            // parentReader.GetOriginal (which throws without a prior registered read).
            if (slotChanges is null && TryReadDeclaredPureRead(in storageCell, out byte[]? value))
            {
                return value;
            }

            return parentReader.GetOriginal(storageCell);
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
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        if (accountChanges.TryGetLastBalanceChangeBefore(_blockAccessIndex, out BalanceChange balanceChange))
        {
            _scratchBalance = balanceChange.Value;
            return ref _scratchBalance;
        }
        if (parentReader.TryGetPureReadAccount(address, out Account? account))
        {
            _scratchBalance = account?.Balance ?? default;
            return ref _scratchBalance;
        }
        return ref parentReader.GetBalance(address);
    }

    public UInt256 GetNonce(Address address)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        if (accountChanges.TryGetLastNonceChangeBefore(_blockAccessIndex, out NonceChange nonceChange))
        {
            return nonceChange.Value;
        }
        return parentReader.TryGetPureReadAccount(address, out Account? account)
            ? account?.Nonce ?? UInt256.Zero
            : parentReader.GetNonce(address);
    }

    public ref readonly ValueHash256 GetCodeHash(Address address)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        if (accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange))
        {
            _scratchCodeHash = codeChange.CodeHash;
            return ref _scratchCodeHash;
        }
        if (parentReader.TryGetPureReadAccount(address, out Account? account))
        {
            _scratchCodeHash = account is not null ? account.CodeHash.ValueHash256 : Keccak.OfAnEmptyString.ValueHash256;
            return ref _scratchCodeHash;
        }
        return ref parentReader.GetCodeHash(address);
    }

    public byte[]? GetCode(Address address)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        if (accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange))
        {
            return codeChange.Code;
        }
        // Pure-read the account for its code hash, then fetch code by hash (no account-journal entry).
        return parentReader.TryGetPureReadAccount(address, out Account? account)
            ? account is null ? [] : parentReader.GetCode(account.CodeHash.ValueHash256)
            : parentReader.GetCode(address);
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
        => TryGetDeclaredCode(in codeHash, out byte[]? code) ? code : null;

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance) => oldBalance = GetBalance(address);

    public void DeleteAccount(Address address) { }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default) { }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

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
        (IWorldState parentReader, ReadOnlyAccountChanges accountChanges) = ResolveContext(address);

        bool parentHasAccount = parentReader.TryGetPureReadAccount(address, out Account? account)
            ? account is not null
            : parentReader.AccountExists(address);
        if (parentHasAccount)
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

        return accountChanges.TryGetLastCodeChangeBefore(_blockAccessIndex, out CodeChange codeChange) &&
               codeChange.Code.Length != 0;
    }

    public bool IsContract(Address address)
        => GetCodeHash(address) != Keccak.OfAnEmptyString;

    public bool IsStorageEmpty(Address address)
    {
        (IWorldState parentReader, _) = ResolveContext(address);
        return parentReader.IsStorageEmpty(address);
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

        ReadOnlySpan<ReadOnlyAccountChanges> accounts = _suggestedBlockAccessList.AccountChanges.AsSpan();
        ArrayPoolList<AddressAsKey> result = new(accounts.Length);
        foreach (ReadOnlyAccountChanges accountChanges in accounts)
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
        if (_suggestedBlockAccessList is null)
            ThrowNotInitialized(nameof(_suggestedBlockAccessList));

        if (_suggestedBlockHeader is null)
            ThrowNotInitialized(nameof(_suggestedBlockHeader));
    }

    private IWorldState GetParentReader()
    {
        if (_parentReader is null)
        {
            ThrowNotInitialized(nameof(_parentReader));
        }

        return _parentReader;
    }

    private ReadOnlyAccountChanges GetAccountChangesOrThrow(Address address)
    {
        Debug.Assert(_suggestedBlockAccessList is not null);
        ReadOnlyAccountChanges? accountChanges = _suggestedBlockAccessList.GetAccountChanges(address);
        if (accountChanges is null)
        {
            ThrowMissingAccount(address);
        }

        return accountChanges;
    }

    private (IWorldState ParentReader, ReadOnlyAccountChanges AccountChanges) ResolveContext(Address address)
    {
        CheckInitialized();
        if (!address.Equals(_contextAccount))
        {
            // New account: re-resolve its BAL changes and invalidate the coverage plan index + cursor.
            _contextChanges = GetAccountChangesOrThrow(address);
            _contextAccount = address;
            _coveragePlanResolved = false;
            _coverageCursor = -1;
        }
        return (GetParentReader(), _contextChanges!);
    }

    // Invalidates the per-account context cache. Called per slice in Setup and when the parent reader
    // detaches; ResolveContext re-resolves on the next distinct address.
    private void ResetContextCache()
    {
        _contextAccount = null;
        _contextChanges = null;
        _coverageHasAccount = false;
        _coveragePlanResolved = false;
        _coverageCursor = -1;
    }

    private bool TryGetDeclaredCode(in ValueHash256 codeHash, [NotNullWhen(true)] out byte[]? code)
    {
        code = null;

        if (_codeChangesByHash is { } codeChangesByHash
            && codeChangesByHash.TryGetValue(codeHash, out (uint Index, byte[] Code) entry)
            && entry.Index < _blockAccessIndex)
        {
            code = entry.Code;
            return true;
        }
        return false;
    }

    private Dictionary<ValueHash256, (uint Index, byte[] Code)>? BuildCodeChangesByHash()
    {
        if (_suggestedBlockAccessList is null)
        {
            return null;
        }

        // Built once per block; entries are immutable across the block. TryGetCodeByHash filters
        // by Index < _blockAccessIndex at lookup time so future-tx code stays invisible. The
        // dictionary itself is only allocated when at least one account declares a code change,
        // so most blocks (which rarely contain deployments) skip the per-block allocation.
        Dictionary<ValueHash256, (uint Index, byte[] Code)>? codeChangesByHash = null;
        foreach (ReadOnlyAccountChanges accountChanges in _suggestedBlockAccessList.AccountChanges)
        {
            ReadOnlySpan<CodeChange> codeChanges = accountChanges.CodeChanges;
            if (codeChanges.Length == 0) continue;
            codeChangesByHash ??= new(GenericEqualityComparer.GetOptimized<ValueHash256>());
            foreach (CodeChange codeChange in codeChanges)
            {
                if (!codeChangesByHash.TryGetValue(codeChange.CodeHash, out (uint Index, byte[] Code) existing)
                    || codeChange.Index < existing.Index)
                {
                    codeChangesByHash[codeChange.CodeHash] = (codeChange.Index, codeChange.Code);
                }
            }
        }

        return codeChangesByHash;
    }

    private static bool TryGetDeclaredSlotChanges(ReadOnlyAccountChanges accountChanges, UInt256 slot, out ReadOnlySlotChanges? slotChanges)
    {
        if (accountChanges.TryGetSlotChanges(slot, out slotChanges))
        {
            return true;
        }

        if (accountChanges.IsStorageRead(slot))
        {
            slotChanges = null;
            return true;
        }

        return false;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowParentReaderAlreadyAttached()
        => throw new InvalidOperationException($"{nameof(_parentReader)} is already attached.");

    [DoesNotReturn, StackTraceHidden]
    private void ThrowMissingAccount(Address address)
        => throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader!, $"Suggested block-level access list missing account changes for {address} at index {_blockAccessIndex}.");

    [DoesNotReturn, StackTraceHidden]
    private void ThrowMissingStorage(in StorageCell storageCell)
        => throw new InvalidBlockLevelAccessListException(_suggestedBlockHeader!, $"Storage access for {storageCell.Address} not in block access list at index {_blockAccessIndex}.");
}
