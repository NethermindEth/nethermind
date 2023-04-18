// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Witnesses;
using Nethermind.Trie;

namespace Nethermind.State;

public partial class WorldState
{
    private const int StateStartCapacity = Resettable.StartCapacity;
    private readonly ResettableDictionary<Address, Stack<int>> _intraBlockCacheForState = new ResettableDictionary<Address, Stack<int>>();
    private readonly ResettableHashSet<Address> _stateCommittedThisRound = new ResettableHashSet<Address>();

    private readonly List<StateChange> _stateKeptInCache = new List<StateChange>();
    private readonly IKeyValueStore _codeDb;

    private int _stateCapacity = StateStartCapacity;
    private StateChange?[] _stateChanges = new StateChange?[StateStartCapacity];
    private int _stateCurrentPosition = Resettable.EmptyPosition;
    private bool _needsStateRootUpdate;
    private readonly StateTree _stateTree;

    public Keccak StateRoot
    {
        get
        {
            if (_needsStateRootUpdate)
            {
                throw new InvalidOperationException();
            }

            return _stateTree.RootHash;
        }
        set => _stateTree.RootHash = value;
    }

    private void CommitState(IReleaseSpec releaseSpec, bool isGenesis = false)
    {
        CommitState(releaseSpec, NullStateTracer.Instance, isGenesis);
    }

    private void CommitState(IReleaseSpec releaseSpec, IStateTracer stateTracer, bool isGenesis = false)
    {
        if (_stateCurrentPosition == -1)
        {
            if (_logger.IsTrace) _logger.Trace("  no state changes to commit");
            return;
        }

        if (_logger.IsTrace) _logger.Trace($"Committing state changes (at {_stateCurrentPosition})");
        if (_stateChanges[_stateCurrentPosition] is null)
        {
            throw new InvalidOperationException($"Change at current position {_stateCurrentPosition} was null when commiting {nameof(WorldState)}");
        }

        if (_stateChanges[_stateCurrentPosition + 1] is not null)
        {
            throw new InvalidOperationException($"Change after current position ({_stateCurrentPosition} + 1) was not null when commiting {nameof(WorldState)}");
        }

        bool isTracing = stateTracer.IsTracingState;
        Dictionary<Address, StateChangeTrace> trace = null;
        if (isTracing)
        {
            trace = new Dictionary<Address, StateChangeTrace>();
        }

        for (int i = 0; i <= _stateCurrentPosition; i++)
        {
            StateChange stateChange = _stateChanges[_stateCurrentPosition - i];
            if (!isTracing && stateChange!.StateChangeType == StateChangeType.JustCache)
            {
                continue;
            }

            if (_stateCommittedThisRound.Contains(stateChange!.Address))
            {
                if (isTracing && stateChange.StateChangeType == StateChangeType.JustCache)
                {
                    trace[stateChange.Address] = new StateChangeTrace(stateChange.Account, trace[stateChange.Address].After);
                }

                continue;
            }

            // because it was not committed yet it means that the just cache is the only state (so it was read only)
            if (isTracing && stateChange.StateChangeType == StateChangeType.JustCache)
            {
                _stateReadsForTracing.Add(stateChange.Address);
                continue;
            }

            int forAssertion = _intraBlockCacheForState[stateChange.Address].Pop();
            if (forAssertion != _stateCurrentPosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_stateCurrentPosition} - {i}");
            }

            _stateCommittedThisRound.Add(stateChange.Address);

            switch (stateChange.StateChangeType)
            {
                case StateChangeType.JustCache:
                    {
                        break;
                    }
                case StateChangeType.Touch:
                case StateChangeType.Update:
                    {
                        if (releaseSpec.IsEip158Enabled && stateChange.Account.IsEmpty && !isGenesis)
                        {
                            if (_logger.IsTrace) _logger.Trace($"  Commit remove empty {stateChange.Address} B = {stateChange.Account.Balance} N = {stateChange.Account.Nonce}");
                            SetState(stateChange.Address, null);
                            if (isTracing)
                            {
                                trace[stateChange.Address] = new StateChangeTrace(null);
                            }
                        }
                        else
                        {
                            if (_logger.IsTrace) _logger.Trace($"  Commit update {stateChange.Address} B = {stateChange.Account.Balance} N = {stateChange.Account.Nonce} C = {stateChange.Account.CodeHash}");
                            SetState(stateChange.Address, stateChange.Account);
                            if (isTracing)
                            {
                                trace[stateChange.Address] = new StateChangeTrace(stateChange.Account);
                            }
                        }

                        break;
                    }
                case StateChangeType.New:
                    {
                        if (!releaseSpec.IsEip158Enabled || !stateChange.Account.IsEmpty || isGenesis)
                        {
                            if (_logger.IsTrace) _logger.Trace($"  Commit create {stateChange.Address} B = {stateChange.Account.Balance} N = {stateChange.Account.Nonce}");
                            SetState(stateChange.Address, stateChange.Account);
                            if (isTracing)
                            {
                                trace[stateChange.Address] = new StateChangeTrace(stateChange.Account);
                            }
                        }

                        break;
                    }
                case StateChangeType.Delete:
                    {
                        if (_logger.IsTrace) _logger.Trace($"  Commit remove {stateChange.Address}");
                        bool wasItCreatedNow = false;
                        while (_intraBlockCacheForState[stateChange.Address].Count > 0)
                        {
                            int previousOne = _intraBlockCacheForState[stateChange.Address].Pop();
                            wasItCreatedNow |= _stateChanges[previousOne].StateChangeType == StateChangeType.New;
                            if (wasItCreatedNow)
                            {
                                break;
                            }
                        }

                        if (!wasItCreatedNow)
                        {
                            SetState(stateChange.Address, null);
                            if (isTracing)
                            {
                                trace[stateChange.Address] = new StateChangeTrace(null);
                            }
                        }

                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (isTracing)
        {
            foreach (Address nullRead in _stateReadsForTracing)
            {
                // // this may be enough, let us write tests
                stateTracer.ReportAccountRead(nullRead);
            }
        }

        Resettable<StateChange>.Reset(ref _stateChanges, ref _stateCapacity, ref _stateCurrentPosition, StateStartCapacity);
        _stateCommittedThisRound.Reset();
        _stateReadsForTracing.Clear();
        _intraBlockCacheForState.Reset();

        if (isTracing)
        {
            ReportChanges(stateTracer, trace);
        }
    }

    public Account GetAccount(Address address)
    {
        return GetThroughCache(address) ?? Account.TotallyEmpty;
    }

    public void RecalculateStateRoot()
    {
        _stateTree.UpdateRootHash();
        _needsStateRootUpdate = false;
    }


    public void DeleteAccount(Address address)
    {
        _needsStateRootUpdate = true;
        PushDelete(address);
    }

    public void CreateAccount(Address address, in UInt256 balance)
    {
        _needsStateRootUpdate = true;
        if (_logger.IsTrace) _logger.Trace($"Creating account: {address} with balance {balance}");
        Account account = balance.IsZero ? Account.TotallyEmpty : new Account(balance);
        PushNew(address, account);
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce)
    {
        _needsStateRootUpdate = true;
        if (_logger.IsTrace) _logger.Trace($"Creating account: {address} with balance {balance} and nonce {nonce}");
        Account account = (balance.IsZero && nonce.IsZero) ? Account.TotallyEmpty : new Account(nonce, balance, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
        PushNew(address, account);
    }

    public void InsertCode(Address address, ReadOnlyMemory<byte> code, IReleaseSpec releaseSpec, bool isGenesis = false)
    {
        _needsStateRootUpdate = true;
        Keccak codeHash;
        if (code.Length == 0)
        {
            codeHash = Keccak.OfAnEmptyString;
        }
        else
        {
            codeHash = Keccak.Compute(code.Span);
            _codeDb[codeHash.Bytes] = code.ToArray();
        }

        Account? account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when updating code hash");
        }

        if (account.CodeHash != codeHash)
        {
            if (_logger.IsTrace) _logger.Trace($"  Update {address} C {account.CodeHash} -> {codeHash}");
            Account changedAccount = account.WithChangedCodeHash(codeHash);
            PushUpdate(address, changedAccount);
        }
        else if (releaseSpec.IsEip158Enabled && !isGenesis)
        {
            if (_logger.IsTrace) _logger.Trace($"  Touch {address} (code hash)");
            if (account.IsEmpty)
            {
                PushTouch(address, account, releaseSpec, account.Balance.IsZero);
            }
        }
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        _needsStateRootUpdate = true;
        SetNewBalance(address, balanceChange, spec, false);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        _needsStateRootUpdate = true;
        SetNewBalance(address, balanceChange, spec, true);
    }

    public void UpdateStorageRoot(Address address, Keccak storageRoot)
    {
        _needsStateRootUpdate = true;
        Account? account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when updating storage hash");
        }

        if (account.StorageRoot != storageRoot)
        {
            if (_logger.IsTrace) _logger.Trace($"  Update {address} S {account.StorageRoot} -> {storageRoot}");
            Account changedAccount = account.WithChangedStorageRoot(storageRoot);
            PushUpdate(address, changedAccount);
        }
    }

    public void IncrementNonce(Address address)
    {
        _needsStateRootUpdate = true;
        Account? account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
        }

        Account changedAccount = account.WithChangedNonce(account.Nonce + 1);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce} -> {changedAccount.Nonce}");
        PushUpdate(address, changedAccount);
    }

    public void DecrementNonce(Address address)
    {
        _needsStateRootUpdate = true;
        Account account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when decrementing nonce.");
        }

        Account changedAccount = account.WithChangedNonce(account.Nonce - 1);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce} -> {changedAccount.Nonce}");
        PushUpdate(address, changedAccount);
    }

    public void TouchCode(Keccak codeHash)
    {
        if (_codeDb is WitnessingStore witnessingStore)
        {
            witnessingStore.Touch(codeHash.Bytes);
        }
    }

    public UInt256 GetNonce(Address address)
    {
        Account? account = GetThroughCache(address);
        return account?.Nonce ?? UInt256.Zero;
    }

    public UInt256 GetBalance(Address address)
    {
        Account? account = GetThroughCache(address);
        return account?.Balance ?? UInt256.Zero;
    }

    public Keccak GetStorageRoot(Address address)
    {
        Account? account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when accessing storage root");
        }

        return account.StorageRoot;
    }

    public byte[] GetCode(Address address)
    {
        Account? account = GetThroughCache(address);
        if (account is null)
        {
            return Array.Empty<byte>();
        }

        return GetCode(account.CodeHash);
    }

    public byte[] GetCode(Keccak codeHash)
    {
        byte[]? code = codeHash == Keccak.OfAnEmptyString ? Array.Empty<byte>() : _codeDb[codeHash.Bytes];
        if (code is null)
        {
            throw new InvalidOperationException($"Code {codeHash} is missing from the database.");
        }

        return code;
    }

    public Keccak GetCodeHash(Address address)
    {
        Account account = GetThroughCache(address);
        return account?.CodeHash ?? Keccak.OfAnEmptyString;
    }

    public void Accept(ITreeVisitor visitor, Keccak stateRoot, VisitingOptions? visitingOptions = null)
    {
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));
        if (stateRoot is null) throw new ArgumentNullException(nameof(stateRoot));

        _stateTree.Accept(visitor, stateRoot, visitingOptions);
    }

    public bool AccountExists(Address address)
    {
        if (_intraBlockCacheForState.TryGetValue(address, out Stack<int> value))
        {
            return _stateChanges[value.Peek()]!.StateChangeType != StateChangeType.Delete;
        }

        return GetAndAddToCache(address) is not null;
    }

    public bool IsDeadAccount(Address address)
    {
        Account? account = GetThroughCache(address);
        return account?.IsEmpty ?? true;
    }

    public bool IsEmptyAccount(Address address)
    {
        Account account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when checking if empty");
        }

        return account.IsEmpty;
    }

    // used in EthereumTests
    public void SetNonce(Address address, in UInt256 nonce)
    {
        _needsStateRootUpdate = true;
        Account? account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
        }

        Account changedAccount = account.WithChangedNonce(nonce);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce} -> {changedAccount.Nonce}");
        PushUpdate(address, changedAccount);
    }

    private void CommitStateTree(long blockNumber)
    {
        if (_needsStateRootUpdate)
        {
            RecalculateStateRoot();
        }

        _stateTree.Commit(blockNumber);
    }

    private void ResetState()
    {
        if (_logger.IsTrace) _logger.Trace("Clearing state provider caches");
        _intraBlockCacheForState.Reset();
        _stateCommittedThisRound.Reset();
        _stateReadsForTracing.Clear();
        if (_codeDb is ReadOnlyDb db) db.ClearTempChanges();
        _stateCurrentPosition = Resettable.EmptyPosition;
        Array.Clear(_stateChanges, 0, _stateChanges.Length);
        _needsStateRootUpdate = false;
    }

    internal void RestoreStateSnapshot(int snapshot)
    {
        if (snapshot > _stateCurrentPosition)
        {
            throw new InvalidOperationException($"{nameof(WorldState)} tried to restore snapshot {snapshot} beyond current position {_stateCurrentPosition}");
        }

        if (_logger.IsTrace) _logger.Trace($"Restoring state snapshot {snapshot}");
        if (snapshot == _stateCurrentPosition)
        {
            return;
        }

        for (int i = 0; i < _stateCurrentPosition - snapshot; i++)
        {
            StateChange stateChange = _stateChanges[_stateCurrentPosition - i];
            if (_intraBlockCacheForState[stateChange!.Address].Count == 1)
            {
                if (stateChange.StateChangeType == StateChangeType.JustCache)
                {
                    int actualPosition = _intraBlockCacheForState[stateChange.Address].Pop();
                    if (actualPosition != _stateCurrentPosition - i)
                    {
                        throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_stateCurrentPosition} - {i}");
                    }

                    _stateKeptInCache.Add(stateChange);
                    _stateChanges[actualPosition] = null;
                    continue;
                }
            }

            _stateChanges[_stateCurrentPosition - i] = null; // TODO: temp, ???
            int forChecking = _intraBlockCacheForState[stateChange.Address].Pop();
            if (forChecking != _stateCurrentPosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forChecking} to be equal to {_stateCurrentPosition} - {i}");
            }

            if (_intraBlockCacheForState[stateChange.Address].Count == 0)
            {
                _intraBlockCacheForState.Remove(stateChange.Address);
            }
        }

        _stateCurrentPosition = snapshot;
        foreach (StateChange kept in _stateKeptInCache)
        {
            _stateCurrentPosition++;
            _stateChanges[_stateCurrentPosition] = kept;
            _intraBlockCacheForState[kept.Address].Push(_stateCurrentPosition);
        }

        _stateKeptInCache.Clear();
    }

    private void SetNewBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSubtracting)
    {
        _needsStateRootUpdate = true;

        Account GetThroughCacheCheckExists()
        {
            Account result = GetThroughCache(address);
            if (result is null)
            {
                if (_logger.IsError) _logger.Error("Updating balance of a non-existing account");
                throw new InvalidOperationException("Updating balance of a non-existing account");
            }

            return result;
        }

        bool isZero = balanceChange.IsZero;
        if (isZero)
        {
            if (releaseSpec.IsEip158Enabled)
            {
                Account touched = GetThroughCacheCheckExists();
                if (_logger.IsTrace) _logger.Trace($"  Touch {address} (balance)");
                if (touched.IsEmpty)
                {
                    PushTouch(address, touched, releaseSpec, true);
                }
            }

            return;
        }

        Account account = GetThroughCacheCheckExists();

        if (isSubtracting && account.Balance < balanceChange)
        {
            throw new InsufficientBalanceException(address);
        }

        UInt256 newBalance = isSubtracting ? account.Balance - balanceChange : account.Balance + balanceChange;

        Account changedAccount = account.WithChangedBalance(newBalance);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} B {account.Balance} -> {newBalance} ({(isSubtracting ? "-" : "+")}{balanceChange})");
        PushUpdate(address, changedAccount);
    }

    private void ReportChanges(IStateTracer stateTracer, Dictionary<Address, StateChangeTrace> trace)
    {
        foreach ((Address address, StateChangeTrace change) in trace)
        {
            bool someChangeReported = false;

            Account? before = change.Before;
            Account? after = change.After;

            UInt256? beforeBalance = before?.Balance;
            UInt256? afterBalance = after?.Balance;

            UInt256? beforeNonce = before?.Nonce;
            UInt256? afterNonce = after?.Nonce;

            Keccak? beforeCodeHash = before?.CodeHash;
            Keccak? afterCodeHash = after?.CodeHash;

            if (beforeCodeHash != afterCodeHash)
            {
                byte[]? beforeCode = beforeCodeHash is null
                    ? null
                    : beforeCodeHash == Keccak.OfAnEmptyString
                        ? Array.Empty<byte>()
                        : _codeDb[beforeCodeHash.Bytes];
                byte[]? afterCode = afterCodeHash is null
                    ? null
                    : afterCodeHash == Keccak.OfAnEmptyString
                        ? Array.Empty<byte>()
                        : _codeDb[afterCodeHash.Bytes];

                if (!((beforeCode?.Length ?? 0) == 0 && (afterCode?.Length ?? 0) == 0))
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

    private Account? GetState(Address address)
    {
        Db.Metrics.StateTreeReads++;
        Account? account = _stateTree.Get(address);
        return account;
    }

    private void SetState(Address address, Account? account)
    {
        _needsStateRootUpdate = true;
        Db.Metrics.StateTreeWrites++;
        _stateTree.Set(address, account);
    }

    private readonly HashSet<Address> _stateReadsForTracing = new HashSet<Address>();

    private Account? GetAndAddToCache(Address address)
    {
        Account? account = GetState(address);
        if (account is not null)
        {
            PushJustCache(address, account);
        }
        else
        {
            // just for tracing - potential perf hit, maybe a better solution?
            _stateReadsForTracing.Add(address);
        }

        return account;
    }

    private Account? GetThroughCache(Address address)
    {
        if (_intraBlockCacheForState.TryGetValue(address, out Stack<int> value))
        {
            return _stateChanges[value.Peek()]!.Account;
        }

        Account account = GetAndAddToCache(address);
        return account;
    }

    private void PushJustCache(Address address, Account account)
    {
        Push(StateChangeType.JustCache, address, account);
    }

    private void PushUpdate(Address address, Account account)
    {
        Push(StateChangeType.Update, address, account);
    }

    private void PushTouch(Address address, Account account, IReleaseSpec releaseSpec, bool isZero)
    {
        if (isZero && releaseSpec.IsEip158IgnoredAccount(address)) return;
        Push(StateChangeType.Touch, address, account);
    }

    private void PushDelete(Address address)
    {
        Push(StateChangeType.Delete, address, null);
    }

    private void Push(StateChangeType stateChangeType, Address address, Account? touchedAccount)
    {
        SetupCache(address);
        if (stateChangeType == StateChangeType.Touch
            && _stateChanges[_intraBlockCacheForState[address].Peek()]!.StateChangeType == StateChangeType.Touch)
        {
            return;
        }

        IncrementStateChangePosition();
        _intraBlockCacheForState[address].Push(_stateCurrentPosition);
        _stateChanges[_stateCurrentPosition] = new StateChange(stateChangeType, address, touchedAccount);
    }

    private void PushNew(Address address, Account account)
    {
        SetupCache(address);
        IncrementStateChangePosition();
        _intraBlockCacheForState[address].Push(_stateCurrentPosition);
        _stateChanges[_stateCurrentPosition] = new StateChange(StateChangeType.New, address, account);
    }

    private void IncrementStateChangePosition()
    {
        Resettable<StateChange>.IncrementPosition(ref _stateChanges, ref _stateCapacity, ref _stateCurrentPosition);
    }

    private void SetupCache(Address address)
    {
        if (!_intraBlockCacheForState.ContainsKey(address))
        {
            _intraBlockCacheForState[address] = new Stack<int>();
        }
    }

    private readonly struct StateChangeTrace
    {
        public StateChangeTrace(Account? before, Account? after)
        {
            After = after;
            Before = before;
        }

        public StateChangeTrace(Account? after)
        {
            After = after;
            Before = null;
        }

        public Account? Before { get; }
        public Account? After { get; }
    }

    private enum StateChangeType
    {
        JustCache,
        Touch,
        Update,
        New,
        Delete
    }

    private class StateChange
    {
        public StateChange(StateChangeType type, Address address, Account? account)
        {
            StateChangeType = type;
            Address = address;
            Account = account;
        }

        public StateChangeType StateChangeType { get; }
        public Address Address { get; }
        public Account? Account { get; }
    }
}
