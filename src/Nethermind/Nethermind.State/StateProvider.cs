// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using static Nethermind.State.StateProvider;

namespace Nethermind.State;

internal partial class StateProvider(ILogManager logManager, LocalMetrics metrics) : IJournal<int>
{
    private readonly LocalMetrics _metrics = metrics;


    // Address -> index of its newest change in _changes; older changes reachable via Change.PrevIdx.
    private readonly OptimizedDictionary<AddressAsKey, int> _intraTxCache = [];
    private readonly OptimizedHashSet<AddressAsKey> _committedThisRound = [];
    private readonly OptimizedHashSet<AddressAsKey> _nullAccountReads = [];
    // Only guarding against hot duplicates within the current block; the cross-block
    // "already persisted" hint now lives on ICodeDb itself (see ICodeDb.ContainsCode).
    // This is intentional: the lifetime of "is this code durably persisted" must match
    // the lifetime of the durable storage, not the StateProvider — otherwise a transient
    // (overlay) codeDb could poison the hint with non-durable entries.
    private readonly AssociativeKeyCache<ValueHash256> _blockCodeInsertFilter = new(256);
    // Code staged for CodeDb by the current transaction, paired with the change-log position of the
    // code-hash update referencing it, so Restore can drop code whose deployment an ancestor frame reverted.
    private readonly List<(int Position, ValueHash256 CodeHash, int Length)> _codeInsertJournal = [];
    private readonly OptimizedDictionary<AddressAsKey, ChangeTrace> _blockChanges = new(4_096);
    private List<AddressAsKey> _removedWithStorage = [];
    // Handed back by a detached write-back once it is done with the list it took.
    private List<AddressAsKey>? _spareRemovedWithStorage;

    private readonly List<Change> _keptInCache = [];
    private readonly ILogger _logger = logManager?.GetClassLogger<StateProvider>() ?? throw new ArgumentNullException(nameof(logManager));
    private Dictionary<Hash256AsKey, byte[]>? _codeBatch;
    private Dictionary<Hash256AsKey, byte[]>.AlternateLookup<ValueHash256> _codeBatchAlternate;
    // EIP-8360 TCREATE code: readable until the transaction ends and never written to CodeDb.
    private Dictionary<Hash256AsKey, byte[]>? _transientCode;
    private Dictionary<Hash256AsKey, byte[]>.AlternateLookup<ValueHash256> _transientCodeAlternate;

    private readonly List<Change> _changes = new(Resettable.StartCapacity);
    // Positions in _changes where each transaction still in the journal started; GetOriginalBalance reads below the top one.
    private readonly Stack<int> _transactionChangesSnapshots = new();
    internal IWorldStateScopeProvider.IScope? _tree;

    private bool _needsStateRootUpdate;
    private IWorldStateScopeProvider.ICodeDb? _codeDb;

    private IWorldStateScopeProvider.IScope Tree =>
        _tree ?? throw new InvalidOperationException("State can only be used within a world-state scope.");

    private IWorldStateScopeProvider.ICodeDb CodeDb =>
        _codeDb ?? throw new InvalidOperationException("Code storage can only be used within a world-state scope.");

    // Invalidates the front cache when a restore/commit/reset recycles the change stacks.
    private void InvalidateFrontCache() => _epoch++;
    // Single-entry cache in front of _intraTxCache: the EVM accesses the same
    // account many times in a row. Pushes write the new value through when the
    // cached address matches, so a hit needs no staleness probe. Invalidated
    // when a restore/commit/reset recycles the change log (epoch).
    private Address? _cachedAddress;
    // Position in _changes of the cached account's head, or -1 for an account that does not exist.
    private int _cachedIndex;
    // Word-sized: every account read compares them, and the zkVM guest pays several times an aligned load for a narrow one.
    private long _cachedEpoch = -1;
    private long _epoch;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsFrontCacheHit(Address address) =>
        _cachedEpoch == _epoch && _cachedAddress is not null && _cachedAddress.Equals(address);

    public void RecalculateStateRoot()
    {
        Tree.UpdateRootHash();
        _needsStateRootUpdate = false;
    }

    public Hash256 StateRoot
    {
        get
        {
            if (_needsStateRootUpdate) ThrowStateRootNeedsToBeUpdated();
            return Tree.RootHash;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowStateRootNeedsToBeUpdated() => throw new InvalidOperationException("State root needs to be updated");
        }
    }

    public int ChangedAccountCount => _blockChanges.Count;

    public void SetScope(IWorldStateScopeProvider.IScope? scope)
    {
        _tree = scope;
        _codeDb = scope?.CodeDb;
    }

    public bool IsContract(Address address) => GetThroughCache(address).IsContract;

    public bool AccountExists(Address address) => GetThroughCache(address).Exists;

    public Account GetAccount(Address address) => GetThroughCache(address).ToAccount() ?? Account.TotallyEmpty;

    public bool IsDeadAccount(Address address)
    {
        ref readonly AccountValue account = ref GetThroughCache(address);
        return !account.Exists || account.IsEmpty;
    }

    public ulong GetNonce(Address address) => GetThroughCache(address).Nonce;

    public UInt256 GetBalance(Address address) => GetThroughCache(address).Balance;

    /// <summary>Returns the balance <paramref name="address"/> had at the start of the current transaction.</summary>
    /// <remarks>
    /// The transaction starts at the last transaction snapshot, or at the last commit when there is none. Walks the
    /// account's changes back to the newest one at or before that point; with none, the committed state holds the value.
    /// </remarks>
    public UInt256 GetOriginalBalance(Address address)
    {
        int transactionStart = _transactionChangesSnapshots.TryPeek(out int snapshot) ? snapshot : Resettable.EmptyPosition;
        if (_intraTxCache.TryGetValue(address, out int index))
        {
            ReadOnlySpan<Change> changes = CollectionsMarshal.AsSpan(_changes);
            while (index > transactionStart)
            {
                index = changes[index].PrevIdx;
                if (index == -1) return GetState(address).Balance;
            }

            return changes[index].Account.Balance;
        }

        return GetState(address).Balance;
    }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        bool inserted = false;

        // Read the account first: for a cold one this appends the JustCache change, so the anchor
        // taken below is the position the code-hash update occupies rather than one before it.
        AccountValue account = GetAccountForCodeUpdate(address);

        // Don't reinsert if already inserted. This can be the case when the same
        // code is used by multiple deployments. Either from factory contracts (e.g. LPs)
        // or people copy and pasting popular contracts
        if (!_blockCodeInsertFilter.Get(codeHash) && !CodeDb.ContainsCode(codeHash))
        {
            if (_codeBatch is null)
            {
                _codeBatch = new(Hash256AsKeyComparer.Instance);
                _codeBatchAlternate = _codeBatch.GetAlternateLookup<ValueHash256>();
            }

            // Only first-time additions are journaled; an entry staged by an already committed
            // transaction must stay in the batch even if a later frame re-inserts and reverts.
            bool journalCode = !_codeBatchAlternate.ContainsKey(codeHash);

            _codeBatchAlternate[codeHash] = code.AsArray();

            _blockCodeInsertFilter.Set(codeHash);
            inserted = true;

            if (journalCode)
            {
                _codeInsertJournal.Add((_changes.Count, codeHash, code.Length));

                _metrics.IncrementCodeWrites();
                _metrics.IncrementCodeBytesWritten(code.Length);
            }
        }

        SetCodeHash(address, account, in codeHash, spec, isGenesis);
        return inserted;
    }

    /// <summary>Sets the code of an EIP-8360 <c>TCREATE</c> account without staging it for CodeDb.</summary>
    /// <remarks>
    /// The code is kept apart from <see cref="_codeBatch"/> and, unlike it, is not journaled: an entry left by a
    /// reverted frame only keeps content-addressed code readable until the transaction ends. A persistent
    /// deployment of the same code goes through <see cref="InsertCode"/>, which this neither marks nor suppresses.
    /// </remarks>
    public void InsertTransientCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec)
    {
        AccountValue account = GetAccountForCodeUpdate(address);
        if (codeHash != Keccak.OfAnEmptyString.ValueHash256)
        {
            if (_transientCode is null)
            {
                _transientCode = new(Hash256AsKeyComparer.Instance);
                _transientCodeAlternate = _transientCode.GetAlternateLookup<ValueHash256>();
            }

            _transientCodeAlternate.TryAdd(codeHash, code.AsArray());
        }

        SetCodeHash(address, account, in codeHash, spec, isGenesis: false);
    }

    /// <summary>Drops the code of the transaction's EIP-8360 <c>TCREATE</c> accounts, which it has finalized.</summary>
    /// <remarks>Commit and Reset also drop it, for a transaction that ended without finalizing its accounts.</remarks>
    public void ResetTransientCode() => _transientCode?.Clear();

    private void SetCodeHash(Address address, in AccountValue account, in ValueHash256 codeHash, IReleaseSpec spec, bool isGenesis)
    {
        if (account.CodeHash.ValueHash256 != codeHash)
        {
            _needsStateRootUpdate = true;
            if (_logger.IsTrace) TraceUpdate(address, codeHash, account.CodeHash);

            PushUpdate(address, account.WithCodeHash((Hash256)codeHash));
        }
        else if (spec.IsEip158Enabled && !isGenesis)
        {
            if (_logger.IsTrace) Trace(address);
            if (account.IsEmpty)
            {
                PushTouch(address, in account, spec, account.Balance.IsZero);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceUpdate(Address address, in ValueHash256 codeHash, Hash256 oldCodeHash)
            => _logger.Trace($"Update {address} C {oldCodeHash} -> {codeHash}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(Address address) => _logger.Trace($"Touch {address} (code hash)");
    }

    private AccountValue GetAccountForCodeUpdate(Address address)
    {
        ref readonly AccountValue account = ref GetThroughCache(address);
        return account.Exists ? account : ThrowIfNull(address);

        [DoesNotReturn, StackTraceHidden]
        static AccountValue ThrowIfNull(Address address)
            => throw new InvalidOperationException($"Account {address} is null when updating code hash");
    }

    private void SetNewBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSubtracting, out UInt256 oldBalance)
    {
        _needsStateRootUpdate = true;

        bool isZero = balanceChange.IsZero;
        if (isZero)
        {
            // this also works like this in Geth (they don't follow the spec ¯\_(*~*)_/¯)
            // however we don't do it because of a consensus issue with Geth, just to avoid
            // hitting non-existing account when subtracting Zero-value from the sender
            if (releaseSpec.IsEip158Enabled && !isSubtracting)
            {
                ref readonly AccountValue touched = ref GetThroughCacheCheckExists(address);

                if (_logger.IsTrace) TraceTouch(address);
                if (touched.IsEmpty)
                {
                    PushTouch(address, in touched, releaseSpec, true);
                }
            }

            oldBalance = 0;
            return;
        }

        ref readonly AccountValue account = ref GetThroughCacheCheckExists(address);

        if (isSubtracting && account.Balance < balanceChange)
        {
            ThrowInsufficientBalanceException(address);
        }

        oldBalance = account.Balance;
        UInt256 newBalance = isSubtracting ? account.Balance - balanceChange : account.Balance + balanceChange;

        if (_logger.IsTrace) TraceUpdate(address, in balanceChange, isSubtracting, in oldBalance, in newBalance);

        PushUpdate(address, account.WithBalance(newBalance));

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceTouch(Address address) => _logger.Trace($"Touch {address} (balance)");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceUpdate(Address address, in UInt256 balanceChange, bool isSubtracting, in UInt256 oldBalance, in UInt256 newBalance)
            => _logger.Trace($"Update {address} B {oldBalance.ToHexString(skipLeadingZeros: true)} -> {newBalance.ToHexString(skipLeadingZeros: true)} ({(isSubtracting ? "-" : "+")}{balanceChange})");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowNonExistingAccount()
            => throw new InvalidOperationException("Updating balance of a non-existing account");

        ref readonly AccountValue GetThroughCacheCheckExists(Address address)
        {
            ref readonly AccountValue result = ref GetThroughCache(address);
            if (!result.Exists) ThrowNonExistingAccount();
            return ref result;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInsufficientBalanceException(Address address)
            => throw new InsufficientBalanceException(address);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec)
        => SubtractFromBalance(address, balanceChange, releaseSpec, out _);
    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, out UInt256 oldBalance)
        => SetNewBalance(address, balanceChange, releaseSpec, true, out oldBalance);

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec)
        => AddToBalance(address, balanceChange, releaseSpec, out _);

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, out UInt256 oldBalance)
        => SetNewBalance(address, balanceChange, releaseSpec, false, out oldBalance);

    public void IncrementNonce(Address address, ulong delta)
        => IncrementNonce(address, delta, out _);

    public void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
    {
        _needsStateRootUpdate = true;
        ref readonly AccountValue account = ref GetThroughCache(address);
        if (!account.Exists) ThrowNullAccount(address);
        oldNonce = account.Nonce;
        ulong newNonce = oldNonce + delta;
        if (_logger.IsTrace) Trace(address, oldNonce, newNonce);

        PushUpdate(address, account.WithNonce(newNonce));

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(Address address, ulong oldNonce, ulong newNonce)
            => _logger.Trace($"Update {address} N {oldNonce.ToHexString(skipLeadingZeros: true)} -> {newNonce.ToHexString(skipLeadingZeros: true)}");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowNullAccount(Address address)
            => throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
    }

    public void DecrementNonce(Address address, ulong delta)
    {
        _needsStateRootUpdate = true;
        ref readonly AccountValue account = ref GetThroughCache(address);
        if (!account.Exists) ThrowNullAccount(address);
        ulong newNonce = account.Nonce - delta;
        if (_logger.IsTrace) Trace(address, account.Nonce, newNonce);

        PushUpdate(address, account.WithNonce(newNonce));

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(Address address, ulong oldNonce, ulong newNonce)
            => _logger.Trace($"  Update {address} N {oldNonce.ToHexString(skipLeadingZeros: true)} -> {newNonce.ToHexString(skipLeadingZeros: true)}");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowNullAccount(Address address)
            => throw new InvalidOperationException($"Account {address} is null when decrementing nonce");
    }

    // Points into the immutable hash object, not the journal, so a later change cannot overwrite it.
    public ref readonly ValueHash256 GetCodeHash(Address address) => ref GetThroughCache(address).CodeHash.ValueHash256;

    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
        => GetCodeCore(in codeHash);

    private ReadOnlyMemory<byte> GetCodeCore(in ValueHash256 codeHash)
    {
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256) return Array.Empty<byte>();

        if (_codeBatch is not null && _codeBatchAlternate.TryGetValue(codeHash, out byte[]? pending))
        {
            return pending;
        }

        if (_transientCode is not null && _transientCodeAlternate.TryGetValue(codeHash, out byte[]? transient))
        {
            return transient;
        }

        ReadOnlyMemory<byte> code = CodeDb.GetCode(codeHash);
        return code.IsNull() ? ThrowMissingCode(in codeHash) : code;

        [DoesNotReturn, StackTraceHidden]
        static ReadOnlyMemory<byte> ThrowMissingCode(in ValueHash256 codeHash)
            => throw new InvalidOperationException($"Code {codeHash} is missing from the database.");
    }

    public ReadOnlyMemory<byte> GetCode(Address address)
    {
        ref readonly AccountValue account = ref GetThroughCache(address);
        if (!account.Exists)
        {
            return Array.Empty<byte>();
        }

        return GetCode(in account.CodeHash.ValueHash256);
    }

    public void DeleteAccount(Address address)
    {
        _needsStateRootUpdate = true;
        PushDelete(address);
    }

    public int TakeSnapshot() => TakeSnapshot(newTransactionStart: false);

    /// <summary>Creates a restartable snapshot.</summary>
    /// <param name="newTransactionStart">A transaction starts here, so <see cref="GetOriginalBalance"/> reads the state before it.</param>
    public int TakeSnapshot(bool newTransactionStart)
    {
        int currentPosition = _changes.Count - 1;
        if (_logger.IsTrace) Trace(currentPosition);
        if (newTransactionStart && currentPosition != Resettable.EmptyPosition)
        {
            _transactionChangesSnapshots.Push(currentPosition);
        }

        return currentPosition;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(int currentPosition) => _logger.Trace($"State snapshot {currentPosition}");
    }

    /// <summary>
    /// Restores the <see cref="StateProvider"/> to a prior state snapshot.
    /// Rolls back any changes recorded after the specified <paramref name="snapshot"/> index,
    /// while preserving lightweight cache-only entries.
    /// </summary>
    /// <param name="snapshot">Zero-based index representing the position in the change log to restore to.
    /// Must be between 0 and the current last change index.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <paramref name="snapshot"/> is beyond the current position,
    /// or if internal consistency checks fail during rollback.</exception>
    public void Restore(int snapshot)
    {
        int lastIndex = _changes.Count - 1;
        if (snapshot > lastIndex) ThrowCannotRestore(lastIndex, snapshot);
        if (_logger.IsTrace) Trace(snapshot);
        // Ahead of the no-op check: an unanchored re-stage sits one past the log, so it is unwound
        // even by a restore to the current position.
        if (_codeInsertJournal.Count > 0) RestoreCodeInserts(snapshot);
        while (_transactionChangesSnapshots.TryPeek(out int transactionStart) && transactionStart > snapshot)
        {
            _transactionChangesSnapshots.Pop();
        }
        // No-op if already at the desired snapshot
        if (snapshot == lastIndex) return;
        InvalidateFrontCache();

        int stepsBack = lastIndex - snapshot;
        // Reserve capacity up‐front (avoid grows)
        if (_keptInCache.Capacity < stepsBack)
            _keptInCache.Capacity = stepsBack;

        ReadOnlySpan<Change> changes = CollectionsMarshal.AsSpan(_changes);
        // Roll back each change from newest down to target
        for (int nextPosition = changes.Length - 1; nextPosition > snapshot; nextPosition--)
        {
            ref readonly Change change = ref changes[nextPosition];
            ref int head = ref _intraTxCache.GetValueRefOrNullRef(change.Address);

            if (Unsafe.IsNullRef(ref head)) ThrowUnexpectedPosition(nextPosition, -1);
            if (head != nextPosition) ThrowUnexpectedPosition(nextPosition, head);

            if (change.PrevIdx != -1)
            {
                head = change.PrevIdx;
            }
            else if (change.ChangeType == ChangeType.JustCache)
            {
                // Keep the read-only entry; its head is stale until re-appended below.
                _keptInCache.Add(change);
            }
            else
            {
                // Remove address entry entirely if no more changes
                _intraTxCache.Remove(change.Address);
            }
        }

        ReadOnlySpan<Change> keepInCache = CollectionsMarshal.AsSpan(_keptInCache);
        // Truncate the change log to the restore point
        CollectionsMarshal.SetCount(_changes, snapshot + 1);

        // Re-append any cache-only entries, updating their positions
        foreach (ref readonly Change kept in keepInCache)
        {
            snapshot++;
            _changes.Add(kept);
            _intraTxCache[kept.Address] = snapshot;
        }
        _keptInCache.Clear();

        // Local helpers to keep cold code from throws and string interpolation out of hot code.
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(int snap) => _logger.Trace($"Restoring state snapshot {snap}");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowCannotRestore(int current, int snap)
            => throw new InvalidOperationException($"{nameof(StateProvider)} tried to restore snapshot {snap} beyond current position {current}");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnexpectedPosition(int expected, int actual)
            => throw new InvalidOperationException($"Expected actual position {actual} to be equal to {expected}");
    }

    /// <summary>
    /// Drops code staged for CodeDb after the given snapshot, so that a reverted contract creation
    /// leaves no runtime bytecode that committed state no longer references.
    /// </summary>
    /// <remarks>
    /// An entry is anchored to the change-log position the code-hash update referencing it occupies, so
    /// <c>position > snapshot</c> selects exactly the entries whose account changes are being unwound.
    /// A re-stage for an account already carrying the hash pushes no update to anchor to, and takes the
    /// position one past the log instead, so every restore unwinds it and no surviving change can
    /// reference it. Dropping it loses nothing: such code is already durable in CodeDb, which is why
    /// the account carries its hash, so the staging is a redundant re-write rather than load-bearing.
    /// The insert filter is rolled back with the batch, otherwise a later surviving deployment of the
    /// same code would be suppressed and lost. The staged-write counters are rolled back too, so they
    /// keep reporting the bytes that actually reach CodeDb.
    /// </remarks>
    private void RestoreCodeInserts(int snapshot)
    {
        ReadOnlySpan<(int Position, ValueHash256 CodeHash, int Length)> entries = CollectionsMarshal.AsSpan(_codeInsertJournal);
        int keep = entries.Length;
        while (keep > 0)
        {
            ref readonly (int Position, ValueHash256 CodeHash, int Length) entry = ref entries[keep - 1];
            if (entry.Position <= snapshot) break;

            _codeBatchAlternate.Remove(entry.CodeHash);
            _blockCodeInsertFilter.Delete(entry.CodeHash);
            _metrics.DecrementCodeWrites(entry.Length);
            keep--;
        }

        CollectionsMarshal.SetCount(_codeInsertJournal, keep);
    }

    public void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
    {
        _needsStateRootUpdate = true;
        if (_logger.IsTrace) Trace(address, balance, nonce);

        PushNew(address, AccountValue.New(nonce, balance));

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(Address address, in UInt256 balance, in ulong nonce)
            => _logger.Trace($"Creating account: {address} with balance {balance.ToHexString(skipLeadingZeros: true)} and nonce {nonce.ToHexString(skipLeadingZeros: true)}");
    }

    // used by Arbitrum
    public void CreateEmptyAccountIfDeletedOrNew(Address address)
    {
        if (_intraTxCache.TryGetValue(address, out int head))
        {
            //we only want to persist empty accounts if they were deleted or created as empty
            //we don't want to do it for account empty due to a change (e.g. changed balance to zero)
            Change lastChange = _changes[head];
            if (lastChange.ChangeType == ChangeType.Delete ||
                (lastChange.ChangeType is ChangeType.Touch or ChangeType.New && lastChange.RequiredAccount.IsEmpty))
            {
                _needsStateRootUpdate = true;
                if (_logger.IsTrace) Trace(address);

                Push(address, AccountValue.TotallyEmpty, ChangeType.RecreateEmpty);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(Address address)
            => _logger.Trace($"Creating zombie account: {address}");
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default)
    {
        if (!AccountExists(address))
        {
            CreateAccount(address, balance, nonce);
        }
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balance, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (AccountExists(address))
        {
            AddToBalance(address, balance, spec, out oldBalance);
            return false;
        }
        else
        {
            oldBalance = 0;
            CreateAccount(address, balance);
            return true;
        }
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer stateTracer, bool commitRoots, bool isGenesis)
    {
        // Committed code can no longer be reverted, and the change-log positions it was journaled
        // against are about to be discarded.
        _codeInsertJournal.Clear();

        Task codeFlushTask = !commitRoots || _codeBatch is null || _codeBatch.Count == 0
            ? Task.CompletedTask
            : CommitCodeAsync(CodeDb);

        bool isTracing = _logger.IsTrace;
        int stepsBack = _changes.Count - 1;
        if (stepsBack < 0)
        {
            if (isTracing) TraceNoChanges();

            ResetTransientCode();
            AwaitCodeFlush(codeFlushTask);
            return;
        }

        if (isTracing) TraceCommit(stepsBack);
        if (_changes[stepsBack].IsNull)
        {
            ThrowStartOfCommitIsNull(stepsBack);
        }

        bool removeEmptyAccounts = releaseSpec.IsEip158Enabled && !isGenesis;
        ReadOnlySpan<Change> changes = CollectionsMarshal.AsSpan(_changes);
        if (stateTracer.IsTracingState)
        {
            Dictionary<AddressAsKey, AccountTrace> trace = [];
            CommitChanges<OnFlag>(changes, removeEmptyAccounts, isTracing, trace);
            // Reporting code changes reads both the code DB and the lookup recycled by the flush worker.
            AwaitCodeFlush(codeFlushTask);
            trace.ReportStateTrace(stateTracer, _nullAccountReads, this);
        }
        else
        {
            CommitChanges<OffFlag>(changes, removeEmptyAccounts, isTracing, null);
        }

        InvalidateFrontCache();
        _changes.Clear();
        _transactionChangesSnapshots.Clear();
        _committedThisRound.ClearAndTrim();
        _nullAccountReads.ClearAndTrim();
        _intraTxCache.ClearAndTrim();
        // A commit ends the transaction, which has finalized its TCREATE accounts by then.
        ResetTransientCode();

        AwaitCodeFlush(codeFlushTask);

        // A single processor persists the batch inline, so its task is always the completed one and
        // there is nothing to await; skipping the awaiter keeps the task machinery out of the guest.
        static void AwaitCodeFlush(Task codeFlushTask)
        {
            if (Core.Cpu.RuntimeInformation.IsSingleProcessor)
            {
                Debug.Assert(codeFlushTask.IsCompletedSuccessfully, "A single processor persists the code batch inline.");
                return;
            }

            codeFlushTask.GetAwaiter().GetResult();
        }

        Task CommitCodeAsync(IWorldStateScopeProvider.ICodeDb codeDb)
        {
            Dictionary<Hash256AsKey, byte[]>? dict = Interlocked.Exchange(ref _codeBatch, null);

            if (dict is null)
                return Task.CompletedTask;

            _codeBatchAlternate = default;

            void PersistCodeBatch()
            {
                using (IWorldStateScopeProvider.ICodeSetter batch = codeDb.BeginCodeWrite())
                {
                    // Insert ordered for improved performance
                    using ArrayPoolListRef<KeyValuePair<Hash256AsKey, byte[]>> entries = dict.ToPooledListRef();
                    entries.Sort(static (a, b) => a.Key.CompareTo(b.Key));
                    foreach (KeyValuePair<Hash256AsKey, byte[]> kvp in entries)
                        batch.Set(kvp.Key.Value, kvp.Value);
                }

                // Mark all inserted codes as persisted on the codeDb itself. For durable
                // codeDbs this populates a hint cache used by ContainsCode; for transient
                // (overlay) codeDbs this is a no-op — overlay writes must not be reported
                // as durably persisted.
                foreach (Hash256AsKey kvp in dict.Keys)
                    codeDb.MarkCodePersisted(kvp.Value.ValueHash256);

                // Reuse Dictionary if not already re-initialized
                dict.Clear();

                if (Interlocked.CompareExchange(ref _codeBatch, dict, null) is null)
                    _codeBatchAlternate = dict.GetAlternateLookup<ValueHash256>();
            }
            // A single processor gains nothing from the hop to the pool, and the guest folds it away.
            if (Core.Cpu.RuntimeInformation.IsSingleProcessor)
            {
                PersistCodeBatch();
                return Task.CompletedTask;
            }

            return Task.Run(PersistCodeBatch);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCommit(int currentPosition) => _logger.Trace($"Committing state changes (at {currentPosition})");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceNoChanges() => _logger.Trace("No state changes to commit");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowStartOfCommitIsNull(int currentPosition)
            => throw new InvalidOperationException($"Change at current position {currentPosition} was null when committing {nameof(StateProvider)}");
    }

    private void CommitChanges<TStateTracing>(
        ReadOnlySpan<Change> changes,
        bool removeEmptyAccounts,
        bool isTracing,
        Dictionary<AddressAsKey, AccountTrace>? trace)
        where TStateTracing : struct, IFlag
    {
        Debug.Assert(TStateTracing.IsActive == (trace is not null));

        for (int i = changes.Length - 1; i >= 0; i--)
        {
            ref readonly Change change = ref changes[i];
            if (!TStateTracing.IsActive && change.ChangeType == ChangeType.JustCache)
            {
                // Safe to skip without touching the head: JustCache is always the bottom of its chain.
                Debug.Assert(change.PrevIdx == -1);
                continue;
            }

            // Walking newest first meets an account's head before its older changes, which commit with the head.
            int head = TStateTracing.IsActive ? -1 : _intraTxCache[change.Address];
            Debug.Assert(head <= i || changes[head].Address == change.Address);
            bool alreadyCommitted = TStateTracing.IsActive
                ? _committedThisRound.Contains(change.Address)
                : head > i;
            if (alreadyCommitted)
            {
                if (TStateTracing.IsActive && change.ChangeType == ChangeType.JustCache)
                {
                    RequireTrace(trace).UpdateTrace(change.Address, in change.Account);
                }

                continue;
            }

            // because it was not committed yet it means that the just cache is the only state (so it was read only)
            if (TStateTracing.IsActive && change.ChangeType == ChangeType.JustCache)
            {
                Debug.Assert(change.PrevIdx == -1);
                _nullAccountReads.Add(change.Address);
                continue;
            }

            int forAssertion = TStateTracing.IsActive ? _intraTxCache[change.Address] : head;
            if (forAssertion != i)
            {
                ThrowUnexpectedCommitPosition(i, forAssertion);
            }

            if (TStateTracing.IsActive) _committedThisRound.Add(change.Address);

            switch (change.ChangeType)
            {
                case ChangeType.JustCache:
                    break;
                case ChangeType.Touch:
                case ChangeType.Update:
                    {
                        AccountValue account = change.RequiredAccount;
                        if (removeEmptyAccounts && account.IsEmpty)
                        {
                            if (isTracing) TraceRemoveEmpty(change);
                            SetState(change.Address, AccountValue.None);
                            if (TStateTracing.IsActive) RequireTrace(trace).AddToTrace(change.Address, AccountValue.None);
                        }
                        else
                        {
                            if (isTracing) TraceUpdate(change);
                            SetState(change.Address, in account);
                            if (TStateTracing.IsActive) RequireTrace(trace).AddToTrace(change.Address, in account);
                        }

                        break;
                    }
                case ChangeType.New:
                    {
                        AccountValue account = change.RequiredAccount;
                        if (!removeEmptyAccounts || !account.IsEmpty)
                        {
                            if (isTracing) TraceCreate(change);
                            SetState(change.Address, in account);
                            if (TStateTracing.IsActive) RequireTrace(trace).AddToTrace(change.Address, in account);
                        }

                        break;
                    }
                case ChangeType.RecreateEmpty:
                    {
                        AccountValue account = change.RequiredAccount;
                        if (isTracing) TraceCreate(change);
                        SetState(change.Address, in account);
                        if (TStateTracing.IsActive) RequireTrace(trace).AddToTrace(change.Address, in account);

                        break;
                    }
                case ChangeType.Delete:
                    {
                        if (isTracing) TraceRemove(change);
                        // Only a chain that starts with New proves the account was absent before the commit window.
                        // Block production commits once per block, so a New can sit above the delete of a pre-existing account.
                        int oldest = change.PrevIdx;
                        while (oldest != -1 && changes[oldest].PrevIdx != -1) oldest = changes[oldest].PrevIdx;
                        bool wasItCreatedNow = oldest != -1 && changes[oldest].ChangeType == ChangeType.New;

                        if (!wasItCreatedNow)
                        {
                            SetState(change.Address, AccountValue.None);
                            if (TStateTracing.IsActive) RequireTrace(trace).AddToTrace(change.Address, AccountValue.None);
                        }

                        break;
                    }
                default:
                    ThrowUnknownChangeType();
                    break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Dictionary<AddressAsKey, AccountTrace> RequireTrace(Dictionary<AddressAsKey, AccountTrace>? trace) =>
        trace ?? throw new InvalidOperationException("State tracing is active without a trace accumulator.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceRemove(in Change change) => _logger.Trace($"Commit remove {change.Address}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceCreate(in Change change)
        => _logger.Trace($"Commit create {change.Address} B = {change.RequiredAccount.Balance.ToHexString(skipLeadingZeros: true)} N = {change.RequiredAccount.Nonce.ToHexString(skipLeadingZeros: true)}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceUpdate(in Change change)
        => _logger.Trace($"Commit update {change.Address} B = {change.RequiredAccount.Balance.ToHexString(skipLeadingZeros: true)} N = {change.RequiredAccount.Nonce.ToHexString(skipLeadingZeros: true)} C = {change.RequiredAccount.CodeHash}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceRemoveEmpty(in Change change)
        => _logger.Trace($"Commit remove empty {change.Address} B = {change.RequiredAccount.Balance.ToHexString(skipLeadingZeros: true)} N = {change.RequiredAccount.Nonce.ToHexString(skipLeadingZeros: true)}");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowUnknownChangeType() => throw new ArgumentOutOfRangeException("changeType", "Unknown change type.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowUnexpectedCommitPosition(int expected, int actual)
        => throw new InvalidOperationException($"Expected checked value {actual} to be equal to {expected}");

    /// <summary>Forgets the removals of the block just committed, so the next block in the scope reports only its own.</summary>
    internal void ClearRemovedAccounts() => _removedWithStorage.Clear();

    /// <summary>
    /// Whether <paramref name="address"/> has an account at the end of the block, or <see langword="null"/> when the block
    /// never loaded the account through this provider.
    /// </summary>
    internal bool? HasAccountAtBlockEnd(Address address)
    {
        ref ChangeTrace change = ref _blockChanges.GetValueRefOrNullRef(address);
        return Unsafe.IsNullRef(ref change) ? null : change.After.Exists;
    }

    /// <summary>
    /// Copies the final value of every account the block touched, reads included, out of the block's change record.
    /// </summary>
    /// <remarks>
    /// A copy rather than a handover: the record is the scope's account state and the next block keeps reading it.
    /// </remarks>
    /// <returns>The copied accounts; the caller owns the list.</returns>
    internal ArrayPoolList<KeyValuePair<AddressAsKey, Account?>> CopyAccountChanges()
    {
        ArrayPoolList<KeyValuePair<AddressAsKey, Account?>> accounts = new(_blockChanges.Count);
        foreach (AddressAsKey key in _blockChanges.Keys)
        {
            accounts.Add(new KeyValuePair<AddressAsKey, Account?>(key, _blockChanges.GetValueRefOrNullRef(key).GetAfterAccount()));
        }

        return accounts;
    }

    /// <summary>
    /// Hands over the accounts the block removed while they held storage, whether or not the block touched that
    /// storage, leaving an empty list in their place.
    /// </summary>
    /// <returns>The removals; the caller owns the list and returns it with <see cref="ReturnRemovedAccounts"/>.</returns>
    internal List<AddressAsKey> DetachRemovedAccountsWithStorage()
    {
        List<AddressAsKey> removed = _removedWithStorage;
        _removedWithStorage = Interlocked.Exchange(ref _spareRemovedWithStorage, null) ?? [];
        return removed;
    }

    /// <summary>Takes back a list from <see cref="DetachRemovedAccountsWithStorage"/>, to be reused by a later block.</summary>
    internal void ReturnRemovedAccounts(List<AddressAsKey> removed)
    {
        removed.Clear();
        Volatile.Write(ref _spareRemovedWithStorage, removed);
    }

    internal void FlushToTree(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        int writes = 0;
        int skipped = 0;

        foreach (AddressAsKey key in _blockChanges.Keys)
        {
            ref ChangeTrace change = ref _blockChanges.GetValueRefOrNullRef(key);
            if (!change.After.Matches(change.Before))
            {
                Account? after = change.GetAfterAccount();
                change.Before = after;
                writeBatch.Set(key, after);
                writes++;
            }
            else
            {
                skipped++;
            }
        }

        if (writes > 0)
            _metrics.IncrementStateTreeWrites(writes);
        if (skipped > 0)
            _metrics.IncrementStateSkippedWrites(skipped);
    }

    public bool WarmUp(Address address)
        => GetState(address).Exists;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref ChangeTrace GetOrAddBlockChange(AddressAsKey key, out bool exists)
        => ref _blockChanges.GetValueRefOrAddDefault(key, out exists);

    internal Account? GetPureRead(Address address) => GetStateEntry(address).GetAfterAccount();

    /// <summary>Layers a transaction prefix over cached block-start account changes.</summary>
    internal void ApplyAccountOverlay(IStateReadOverlay overlay)
    {
        InvalidateFrontCache();
        _nullAccountReads.ClearAndTrim();
        if (_blockChanges.Count == 0) return;

        foreach (AddressAsKey key in _blockChanges.Keys)
        {
            ref ChangeTrace change = ref _blockChanges.GetValueRefOrNullRef(key);
            if (overlay.TryGetAccount(key.Value, change.GetAfterAccount(), out Account? account)) change.SetAfter(account);
        }
    }

    private ref readonly AccountValue GetState(Address address) => ref GetStateEntry(address).After;

    private ref ChangeTrace GetStateEntry(Address address)
    {
        AddressAsKey addressAsKey = address;
        ref ChangeTrace accountChanges = ref GetOrAddBlockChange(addressAsKey, out bool exists);
        if (!exists)
        {
            _metrics.IncrementStateTreeReads();
            accountChanges = new(Tree.Get(address));
        }
        else
        {
            _metrics.IncrementStateTreeCacheHits();
        }
        return ref accountChanges;
    }

    /// <summary>Records a value the backend reports for <paramref name="address"/>, keeping its object.</summary>
    internal void SetState(Address address, Account? account)
    {
        ref ChangeTrace accountChanges = ref RecordWrite(address, removal: account is null);
        accountChanges.SetAfter(account);
    }

    internal void SetState(Address address, in AccountValue account)
    {
        ref ChangeTrace accountChanges = ref RecordWrite(address, removal: !account.Exists);
        accountChanges.SetAfter(in account);
    }

    private ref ChangeTrace RecordWrite(Address address, bool removal)
    {
        _metrics.IncrementAccountWrites();
        ref ChangeTrace accountChanges = ref GetOrAddBlockChange(address, out bool exists);
        if (removal)
        {
            _metrics.IncrementAccountDeleted();
            // A block that removes an account it never read has nothing to compare against, so the removal would go
            // unrecorded and the storage that goes with the account would stay readable to a cache that cannot infer
            // the wipe from a root. Resolved here rather than through GetState: the block did not read this account,
            // and the read counters must keep saying so.
            if (!exists)
            {
                accountChanges = new(Tree.Get(address));
            }
            // A removal takes the account's storage with it whichever path removed it; caches of that storage learn of it here.
            if (accountChanges.After.Exists && (accountChanges.After.HasStorage || !Tree.StorageRootsAreAuthoritative))
                _removedWithStorage.Add(address);
        }

        _needsStateRootUpdate = true;
        return ref accountChanges;
    }

    /// <returns>The position of the account's new change, or -1 when it does not exist.</returns>
    private int GetAndAddToCache(Address address)
    {
        // Rarely non-empty, and probing an empty set still hashes the key.
        if (_nullAccountReads.Count != 0 && _nullAccountReads.Contains(address)) return -1;

        ref readonly AccountValue account = ref GetState(address);
        if (account.Exists)
        {
            PushJustCache(address, in account);
            return _changes.Count - 1;
        }

        // just for tracing - potential perf hit, maybe a better solution?
        _nullAccountReads.Add(address);
        return -1;
    }

    /// <summary>The account's current value, <see cref="AccountValue.None"/> when it does not exist.</summary>
    /// <remarks>The reference points into the journal: read it before the next change, which can overwrite it.</remarks>
    internal ref readonly AccountValue GetThroughCache(Address address)
    {
        if (!IsFrontCacheHit(address))
        {
            _cachedIndex = _intraTxCache.TryGetValue(address, out int head) ? head : GetAndAddToCache(address);
            _cachedAddress = address;
            _cachedEpoch = _epoch;
        }

        return ref _cachedIndex < 0 ? ref AccountValue.None : ref CollectionsMarshal.AsSpan(_changes)[_cachedIndex].Account;
    }

    private void PushJustCache(Address address, in AccountValue account)
        => Push(address, in account, ChangeType.JustCache);

    private void PushUpdate(Address address, in AccountValue account)
        => Push(address, in account, ChangeType.Update);

    private void PushTouch(Address address, in AccountValue account, IReleaseSpec releaseSpec, bool isZero)
    {
        if (isZero && address == releaseSpec.Eip158IgnoredAccount) return;
        Push(address, in account, ChangeType.Touch);
    }

    private void PushDelete(Address address)
        => Push(address, AccountValue.None, ChangeType.Delete);

    private void PushNew(Address address, in AccountValue account)
        => Push(address, in account, ChangeType.New);

    private void Push(Address address, in AccountValue touchedAccount, ChangeType changeType)
    {
        ref int head = ref _intraTxCache.GetValueRefOrAddDefault(address, out bool exists);
        ChangeType headType = exists ? CollectionsMarshal.AsSpan(_changes)[head].ChangeType : ChangeType.Null;
        if (changeType == ChangeType.Touch && headType == ChangeType.Touch)
        {
            return;
        }

        // Only a change rewrites the account's leaf at commit, so only a change is worth warming its trie path
        // for, and only the transaction's first change of an account needs to say so. No-op for backends
        // without trie warm-up.
        if (changeType != ChangeType.JustCache && (!exists || headType == ChangeType.JustCache))
        {
            _tree?.HintWarmAccount(address);
        }

        int prevIdx = exists ? head : -1;
        head = _changes.Count;
        _changes.Add(new Change(address, in touchedAccount, changeType, prevIdx));
        // Keep the front cache coherent: a push almost always follows a read of the same account.
        if (IsFrontCacheHit(address))
            _cachedIndex = head;
    }

    public ArrayPoolList<AddressAsKey>? ChangedAddresses()
    {
        int count = _blockChanges.Count;
        if (count == 0)
        {
            return null;
        }
        else
        {
            ArrayPoolList<AddressAsKey> addresses = new(count);
            foreach (AddressAsKey address in _blockChanges.Keys)
            {
                addresses.Add(address);
            }
            return addresses;
        }
    }

    /// <summary>
    /// Drops the block's record of every account <paramref name="bal"/> changes, handing its committed value to
    /// <paramref name="scope"/> as the one the BAL is applied over.
    /// </summary>
    /// <remarks>
    /// Call after a commit with roots and before the BAL is applied. A hint only fills an empty slot, so a later hint of
    /// the account's pre-block value (one the caches still hold) cannot override the committed one. Removals with
    /// storage stay recorded: a removed account's cached slots must go whether or not the BAL recreated it.
    /// </remarks>
    internal void ForgetBlockChanges(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IScope scope)
    {
        foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
        {
            if (accountChanges.HasStateChanges && _blockChanges.Remove(accountChanges.Address, out ChangeTrace change))
                scope.HintGet(accountChanges.Address, change.GetAfterAccount());
        }
    }

    /// <summary>Has the write-back drop the cached storage of <paramref name="address"/>, as for an account removed with storage.</summary>
    internal void ForgetCachedStorage(Address address) => _removedWithStorage.Add(address);

    public void Reset(bool resetBlockChanges = true)
    {
        if (_logger.IsTrace) Trace();
        // The changes being discarded are exactly the ones the journal covers (it is cleared on every
        // commit), so unwind it on both paths: otherwise its code either reaches CodeDb unreferenced
        // or, when the batch is dropped below, stays counted as written without ever being written.
        if (_codeInsertJournal.Count > 0) RestoreCodeInserts(Snapshot.EmptyPosition);
        if (resetBlockChanges)
        {
            _blockCodeInsertFilter.Clear();
            _blockChanges.Clear();
            _removedWithStorage.Clear();
            _codeBatch?.Clear();
        }
        ResetTransientCode();
        _intraTxCache.ClearAndTrim();
        _committedThisRound.ClearAndTrim();
        _nullAccountReads.ClearAndTrim();
        InvalidateFrontCache();
        _changes.Clear();
        _transactionChangesSnapshots.Clear();
        _needsStateRootUpdate = false;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace() => _logger.Trace("Clearing state provider caches");
    }

    public void UpdateStateRootIfNeeded()
    {
        if (_needsStateRootUpdate)
        {
            RecalculateStateRoot();
        }
    }

    // used in EthereumTests
    internal void SetNonce(Address address, in ulong nonce)
    {
        _needsStateRootUpdate = true;
        ref readonly AccountValue account = ref GetThroughCache(address);
        if (!account.Exists) ThrowNullAccount(address);
        if (_logger.IsTrace) Trace(address, account.Nonce, nonce);

        PushUpdate(address, account.WithNonce(nonce));

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(Address address, ulong oldNonce, ulong newNonce)
            => _logger.Trace($"Update {address} N {oldNonce} -> {newNonce}");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowNullAccount(Address address)
            => throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
    }

    private readonly struct Change(Address address, in AccountValue account, ChangeType type, int prevIdx)
    {
        public readonly Address Address = address;
        public readonly AccountValue Account = account;
        public readonly ChangeType ChangeType = type;

        /// <summary>Index into <c>_changes</c> of the previous change for the same address, or -1 if none.</summary>
        public readonly int PrevIdx = prevIdx;

        public bool IsNull => ChangeType == ChangeType.Null;
        public AccountValue RequiredAccount => Account.Exists ? Account : throw new InvalidOperationException($"{ChangeType} changes require an account value.");
    }

    /// <summary>An account's fields held inline, so a change to them allocates nothing; <c>default</c> is no account.</summary>
    /// <remarks>
    /// The journal and the block's change record hold these instead of <see cref="Nethermind.Core.Account"/> objects, which
    /// are immutable and so cost one allocation per balance or nonce change. An object is built only where one leaves the
    /// provider, once per changed account per block.
    /// </remarks>
    internal readonly struct AccountValue
    {
        public static readonly AccountValue None = default;
        public static readonly AccountValue TotallyEmpty = new(0, UInt256.Zero, null, null);

        public readonly UInt256 Balance;
        public readonly ulong Nonce;
        private readonly Hash256? _codeHash;
        private readonly Hash256? _storageRoot;
        public readonly bool Exists;

        private AccountValue(ulong nonce, in UInt256 balance, Hash256? codeHash, Hash256? storageRoot)
        {
            Balance = balance;
            Nonce = nonce;
            _codeHash = codeHash;
            _storageRoot = storageRoot;
            Exists = true;
        }

        public static AccountValue From(Account? account) =>
            account is null ? None : new(account.Nonce, account.Balance, account.HasCode ? account.CodeHash : null, account.HasStorage ? account.StorageRoot : null);

        public static AccountValue New(ulong nonce, in UInt256 balance) => new(nonce, balance, null, null);

        public Hash256 CodeHash => _codeHash ?? Keccak.OfAnEmptyString;
        public Hash256 StorageRoot => _storageRoot ?? Keccak.EmptyTreeHash;
        public bool HasStorage => _storageRoot is not null;
        public bool IsContract => _codeHash is not null;
        public bool IsEmpty => _codeHash is null && Balance.IsZero && Nonce == 0;

        public AccountValue WithBalance(in UInt256 balance) => new(Nonce, balance, _codeHash, _storageRoot);
        public AccountValue WithNonce(ulong nonce) => new(nonce, Balance, _codeHash, _storageRoot);
        public AccountValue WithCodeHash(Hash256 codeHash) => new(Nonce, Balance, codeHash == Keccak.OfAnEmptyString ? null : codeHash, _storageRoot);

        public Account? ToAccount() =>
            !Exists ? null
            : _codeHash is null && _storageRoot is null && Nonce == 0 && Balance.IsZero ? Core.Account.TotallyEmpty
            : new Account(Nonce, Balance, StorageRoot, CodeHash);

        public AccountStruct ToStruct(in ValueHash256 storageRoot) => new(Nonce, Balance, storageRoot, CodeHash.ValueHash256);

        /// <summary>Whether <paramref name="account"/> holds the same values, absence included.</summary>
        public bool Matches(Account? account) =>
            account is null ? !Exists
            : Exists && Nonce == account.Nonce && Balance == account.Balance
              && CodeHash == account.CodeHash && StorageRoot == account.StorageRoot;

        public bool Matches(in AccountValue other) =>
            Exists == other.Exists && Nonce == other.Nonce && Balance == other.Balance
            && CodeHash == other.CodeHash && StorageRoot == other.StorageRoot;
    }

    /// <summary>An account as the tree holds it and as the block has left it since the scope's last flush.</summary>
    /// <remarks>Mutable: reach it by reference, or the object <see cref="GetAfterAccount"/> builds is built again.</remarks>
    internal struct ChangeTrace(Account? loaded)
    {
        public Account? Before = loaded;
        /// <summary>The block's value; assigned only through <see cref="SetAfter(in AccountValue)"/>, which keeps the object in step.</summary>
        public AccountValue After = AccountValue.From(loaded);
        // After as an object: the one read from the tree until a change, built on first need after one.
        private Account? _afterAccount = loaded;

        public Account? GetAfterAccount() => _afterAccount ??= After.ToAccount();

        public void SetAfter(in AccountValue after)
        {
            After = after;
            _afterAccount = null;
        }

        public void SetAfter(Account? after)
        {
            After = AccountValue.From(after);
            _afterAccount = after;
        }
    }

    /// <summary>An account's values before and after a commit, as a state trace reports them.</summary>
    internal readonly record struct AccountTrace(AccountValue Before, AccountValue After);
}

internal static class Extensions
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AddToTrace(this Dictionary<AddressAsKey, AccountTrace> trace, Address address, in AccountValue change) => trace.Add(address, new AccountTrace(AccountValue.None, change));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void UpdateTrace(this Dictionary<AddressAsKey, AccountTrace> trace, Address address, in AccountValue change) => trace[address] = new AccountTrace(change, trace[address].After);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ReportStateTrace(this Dictionary<AddressAsKey, AccountTrace> trace, IWorldStateTracer stateTracer, OptimizedHashSet<AddressAsKey> nullAccountReads, StateProvider stateProvider)
    {
        foreach (Address nullRead in nullAccountReads)
        {
            // // this may be enough, let us write tests
            stateTracer.ReportAccountRead(nullRead);
        }
        ReportChanges(trace, stateTracer, stateProvider);
    }

    private static void ReportChanges(Dictionary<AddressAsKey, AccountTrace> trace, IStateTracer stateTracer, StateProvider stateProvider)
    {
        foreach (KeyValuePair<AddressAsKey, AccountTrace> entry in trace)
        {
            Address address = entry.Key;
            (AccountValue before, AccountValue after) = entry.Value;
            bool someChangeReported = false;

            UInt256? beforeBalance = before.Exists ? before.Balance : null;
            UInt256? afterBalance = after.Exists ? after.Balance : null;

            UInt256? beforeNonce = before.Exists ? before.Nonce : null;
            UInt256? afterNonce = after.Exists ? after.Nonce : null;

            Hash256? beforeCodeHash = before.Exists ? before.CodeHash : null;
            Hash256? afterCodeHash = after.Exists ? after.CodeHash : null;

            if (beforeCodeHash != afterCodeHash)
            {
                ReadOnlyMemory<byte> beforeCode = beforeCodeHash is null
                    ? default
                    : beforeCodeHash == Keccak.OfAnEmptyString
                        ? Array.Empty<byte>()
                        : stateProvider.GetCode(in beforeCodeHash.ValueHash256);
                ReadOnlyMemory<byte> afterCode = afterCodeHash is null
                    ? default
                    : afterCodeHash == Keccak.OfAnEmptyString
                        ? Array.Empty<byte>()
                        : stateProvider.GetCode(in afterCodeHash.ValueHash256);

                if (!(beforeCode.IsEmpty && afterCode.IsEmpty))
                {
                    stateTracer.ReportCodeChange(address, beforeCode, afterCode);
                }

                someChangeReported = true;
            }

            if (afterBalance != beforeBalance)
            {
                stateTracer.ReportBalanceChange(address, beforeBalance, afterBalance);
                someChangeReported = true;
            }

            if (afterNonce != beforeNonce)
            {
                stateTracer.ReportNonceChange(address, beforeNonce, afterNonce);
                someChangeReported = true;
            }

            if (!someChangeReported)
            {
                stateTracer.ReportAccountRead(address);
            }
        }
    }
}
