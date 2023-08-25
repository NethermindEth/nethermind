// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;
using Nethermind.State.Witnesses;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.State
{
    internal class StateProvider
    {
        private const int StartCapacity = Resettable.StartCapacity;
        private readonly ResettableDictionary<Address, Stack<int>> _intraBlockCache = new();
        private readonly ResettableHashSet<Address> _committedThisRound = new();
        // Only guarding against hot duplicates so filter doesn't need to be too big
        // Note:
        // False negatives are fine as they will just result in a overwrite set
        // False positives would be problematic as the code _must_ be persisted
        private readonly LruKeyCache<Keccak> _codeInsertFilter = new(2048, "Code Insert Filter");

        private readonly List<Change> _keptInCache = new();
        private readonly ILogger _logger;
        private readonly IKeyValueStore _codeDb;

        private int _capacity = StartCapacity;
        private Change?[] _changes = new Change?[StartCapacity];
        private int _currentPosition = Resettable.EmptyPosition;

        public StateProvider(ITrieStore? trieStore, IKeyValueStore? codeDb, ILogManager? logManager, StateTree? stateTree = null)
        {
            _logger = logManager?.GetClassLogger<StateProvider>() ?? throw new ArgumentNullException(nameof(logManager));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _tree = stateTree ?? new StateTree(trieStore, logManager);
        }

        public void Accept(ITreeVisitor? visitor, Keccak? stateRoot, VisitingOptions? visitingOptions = null)
        {
            if (visitor is null) throw new ArgumentNullException(nameof(visitor));
            if (stateRoot is null) throw new ArgumentNullException(nameof(stateRoot));

            _tree.Accept(visitor, stateRoot, visitingOptions);
        }

        private bool _needsStateRootUpdate;

        public void RecalculateStateRoot()
        {
            _tree.UpdateRootHash();
            _needsStateRootUpdate = false;
        }

        public Keccak StateRoot
        {
            get
            {
                if (_needsStateRootUpdate)
                {
                    throw new InvalidOperationException();
                }

                return _tree.RootHash;
            }
            set => _tree.RootHash = value;
        }

        internal readonly StateTree _tree;

        public bool IsContract(Address address)
        {
            Account? account = GetThroughCache(address);
            if (account is null)
            {
                return false;
            }

            return account.IsContract;
        }

        public bool AccountExists(Address address)
        {
            if (_intraBlockCache.TryGetValue(address, out Stack<int> value))
            {
                return _changes[value.Peek()]!.ChangeType != ChangeType.Delete;
            }

            return GetAndAddToCache(address) is not null;
        }

        public bool IsEmptyAccount(Address address)
        {
            Account? account = GetThroughCache(address);
            if (account is null)
            {
                throw new InvalidOperationException($"Account {address} is null when checking if empty");
            }

            return account.IsEmpty;
        }

        public Account GetAccount(Address address)
        {
            return GetThroughCache(address) ?? Account.TotallyEmpty;
        }

        public bool IsDeadAccount(Address address)
        {
            Account? account = GetThroughCache(address);
            return account?.IsEmpty ?? true;
        }

        public UInt256 GetNonce(Address address)
        {
            Account? account = GetThroughCache(address);
            return account?.Nonce ?? UInt256.Zero;
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

        public UInt256 GetBalance(Address address)
        {
            Account? account = GetThroughCache(address);
            return account?.Balance ?? UInt256.Zero;
        }

        public void InsertCode(Address address, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            _needsStateRootUpdate = true;
            Keccak codeHash = code.Length == 0 ? Keccak.OfAnEmptyString : Keccak.Compute(code.Span);

            // Don't reinsert if already inserted. This can be the case when the same
            // code is used by multiple deployments. Either from factory contracts (e.g. LPs)
            // or people copy and pasting popular contracts
            if (!_codeInsertFilter.Get(codeHash))
            {
                if (_codeDb is IDbWithSpan dbWithSpan)
                {
                    dbWithSpan.PutSpan(codeHash.Bytes, code.Span);
                }
                else if (MemoryMarshal.TryGetArray(code, out ArraySegment<byte> codeArray)
                        && codeArray.Offset == 0
                        && codeArray.Count == code.Length)
                {
                    _codeDb[codeHash.Bytes] = codeArray.Array;
                }
                else
                {
                    _codeDb[codeHash.Bytes] = code.ToArray();
                }

                _codeInsertFilter.Set(codeHash);
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
            else if (spec.IsEip158Enabled && !isGenesis)
            {
                if (_logger.IsTrace) _logger.Trace($"  Touch {address} (code hash)");
                if (account.IsEmpty)
                {
                    PushTouch(address, account, spec, account.Balance.IsZero);
                }
            }
        }

        private void SetNewBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSubtracting)
        {
            _needsStateRootUpdate = true;

            Account GetThroughCacheCheckExists()
            {
                Account result = GetThroughCache(address);
                if (result is null)
                {
                    if (address == Address.SystemUser)
                    {
                        return Account.TotallyEmpty;
                    }

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

        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec)
        {
            _needsStateRootUpdate = true;
            SetNewBalance(address, balanceChange, releaseSpec, true);
        }

        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec)
        {
            _needsStateRootUpdate = true;
            SetNewBalance(address, balanceChange, releaseSpec, false);
        }

        /// <summary>
        /// This is a coupling point between storage provider and state provider.
        /// This is pointing at the architectural change likely required where Storage and State Provider are represented by a single world state class.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="storageRoot"></param>
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
            Account? account = GetThroughCache(address);
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

        public Keccak GetCodeHash(Address address)
        {
            Account? account = GetThroughCache(address);
            return account?.CodeHash ?? Keccak.OfAnEmptyString;
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

        public byte[] GetCode(Address address)
        {
            Account? account = GetThroughCache(address);
            if (account is null)
            {
                return Array.Empty<byte>();
            }

            return GetCode(account.CodeHash);
        }

        public void DeleteAccount(Address address)
        {
            _needsStateRootUpdate = true;
            PushDelete(address);
        }

        public int TakeSnapshot()
        {
            if (_logger.IsTrace) _logger.Trace($"State snapshot {_currentPosition}");
            return _currentPosition;
        }

        public void Restore(int snapshot)
        {
            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"{nameof(StateProvider)} tried to restore snapshot {snapshot} beyond current position {_currentPosition}");
            }

            if (_logger.IsTrace) _logger.Trace($"Restoring state snapshot {snapshot}");
            if (snapshot == _currentPosition)
            {
                return;
            }

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_intraBlockCache[change!.Address].Count == 1)
                {
                    if (change.ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = _intraBlockCache[change.Address].Pop();
                        if (actualPosition != _currentPosition - i)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_currentPosition} - {i}");
                        }

                        _keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                _changes[_currentPosition - i] = null; // TODO: temp, ???
                int forChecking = _intraBlockCache[change.Address].Pop();
                if (forChecking != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forChecking} to be equal to {_currentPosition} - {i}");
                }

                if (_intraBlockCache[change.Address].Count == 0)
                {
                    _intraBlockCache.Remove(change.Address);
                }
            }

            _currentPosition = snapshot;
            foreach (Change kept in _keptInCache)
            {
                _currentPosition++;
                _changes[_currentPosition] = kept;
                _intraBlockCache[kept.Address].Push(_currentPosition);
            }

            _keptInCache.Clear();
        }

        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            _needsStateRootUpdate = true;
            if (_logger.IsTrace) _logger.Trace($"Creating account: {address} with balance {balance} and nonce {nonce}");
            Account account = (balance.IsZero && nonce.IsZero) ? Account.TotallyEmpty : new Account(nonce, balance);
            PushNew(address, account);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            if (AccountExists(address)) return;
            CreateAccount(address, balance, nonce);
        }

        public void AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balance, IReleaseSpec spec)
        {
            if (AccountExists(address))
            {
                AddToBalance(address, balance, spec);
            }
            else
            {
                CreateAccount(address, balance);
            }
        }

        public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
        {
            Commit(releaseSpec, NullStateTracer.Instance, isGenesis);
        }

        private readonly struct ChangeTrace
        {
            public ChangeTrace(Account? before, Account? after)
            {
                After = after;
                Before = before;
            }

            public ChangeTrace(Account? after)
            {
                After = after;
                Before = null;
            }

            public Account? Before { get; }
            public Account? After { get; }
        }

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer stateTracer, bool isGenesis = false)
        {
            if (_currentPosition == -1)
            {
                if (_logger.IsTrace) _logger.Trace("  no state changes to commit");
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Committing state changes (at {_currentPosition})");
            if (_changes[_currentPosition] is null)
            {
                throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(StateProvider)}");
            }

            if (_changes[_currentPosition + 1] is not null)
            {
                throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(StateProvider)}");
            }

            bool isTracing = stateTracer.IsTracingState;
            Dictionary<Address, ChangeTrace> trace = null;
            if (isTracing)
            {
                trace = new Dictionary<Address, ChangeTrace>();
            }

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (!isTracing && change!.ChangeType == ChangeType.JustCache)
                {
                    continue;
                }

                if (_committedThisRound.Contains(change!.Address))
                {
                    if (isTracing && change.ChangeType == ChangeType.JustCache)
                    {
                        trace[change.Address] = new ChangeTrace(change.Account, trace[change.Address].After);
                    }

                    continue;
                }

                // because it was not committed yet it means that the just cache is the only state (so it was read only)
                if (isTracing && change.ChangeType == ChangeType.JustCache)
                {
                    _readsForTracing.Add(change.Address);
                    continue;
                }

                int forAssertion = _intraBlockCache[change.Address].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                _committedThisRound.Add(change.Address);

                switch (change.ChangeType)
                {
                    case ChangeType.JustCache:
                        {
                            break;
                        }
                    case ChangeType.Touch:
                    case ChangeType.Update:
                        {
                            if (releaseSpec.IsEip158Enabled && change.Account.IsEmpty && !isGenesis)
                            {
                                if (_logger.IsTrace) _logger.Trace($"  Commit remove empty {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                                SetState(change.Address, null);
                                if (isTracing)
                                {
                                    trace[change.Address] = new ChangeTrace(null);
                                }
                            }
                            else
                            {
                                if (_logger.IsTrace) _logger.Trace($"  Commit update {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce} C = {change.Account.CodeHash}");
                                SetState(change.Address, change.Account);
                                if (isTracing)
                                {
                                    trace[change.Address] = new ChangeTrace(change.Account);
                                }
                            }

                            break;
                        }
                    case ChangeType.New:
                        {
                            if (!releaseSpec.IsEip158Enabled || !change.Account.IsEmpty || isGenesis)
                            {
                                if (_logger.IsTrace) _logger.Trace($"  Commit create {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                                SetState(change.Address, change.Account);
                                if (isTracing)
                                {
                                    trace[change.Address] = new ChangeTrace(change.Account);
                                }
                            }

                            break;
                        }
                    case ChangeType.Delete:
                        {
                            if (_logger.IsTrace) _logger.Trace($"  Commit remove {change.Address}");
                            bool wasItCreatedNow = false;
                            while (_intraBlockCache[change.Address].Count > 0)
                            {
                                int previousOne = _intraBlockCache[change.Address].Pop();
                                wasItCreatedNow |= _changes[previousOne].ChangeType == ChangeType.New;
                                if (wasItCreatedNow)
                                {
                                    break;
                                }
                            }

                            if (!wasItCreatedNow)
                            {
                                SetState(change.Address, null);
                                if (isTracing)
                                {
                                    trace[change.Address] = new ChangeTrace(null);
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
                foreach (Address nullRead in _readsForTracing)
                {
                    // // this may be enough, let us write tests
                    stateTracer.ReportAccountRead(nullRead);
                }
            }

            Resettable<Change>.Reset(ref _changes, ref _capacity, ref _currentPosition, StartCapacity);
            _committedThisRound.Reset();
            _readsForTracing.Clear();
            _intraBlockCache.Reset();

            if (isTracing)
            {
                ReportChanges(stateTracer, trace);
            }
        }

        private void ReportChanges(IStateTracer stateTracer, Dictionary<Address, ChangeTrace> trace)
        {
            foreach ((Address address, ChangeTrace change) in trace)
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
            Metrics.StateTreeReads++;
            Account? account = _tree.Get(address);
            return account;
        }

        private void SetState(Address address, Account? account)
        {
            _needsStateRootUpdate = true;
            Metrics.StateTreeWrites++;
            _tree.Set(address, account);
        }

        private readonly HashSet<Address> _readsForTracing = new();

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
                _readsForTracing.Add(address);
            }

            return account;
        }

        private Account? GetThroughCache(Address address)
        {
            if (_intraBlockCache.TryGetValue(address, out Stack<int> value))
            {
                return _changes[value.Peek()]!.Account;
            }

            Account account = GetAndAddToCache(address);
            return account;
        }

        private void PushJustCache(Address address, Account account)
        {
            Push(ChangeType.JustCache, address, account);
        }

        private void PushUpdate(Address address, Account account)
        {
            Push(ChangeType.Update, address, account);
        }

        private void PushTouch(Address address, Account account, IReleaseSpec releaseSpec, bool isZero)
        {
            if (isZero && releaseSpec.IsEip158IgnoredAccount(address)) return;
            Push(ChangeType.Touch, address, account);
        }

        private void PushDelete(Address address)
        {
            Push(ChangeType.Delete, address, null);
        }

        private void Push(ChangeType changeType, Address address, Account? touchedAccount)
        {
            SetupCache(address);
            if (changeType == ChangeType.Touch
                && _changes[_intraBlockCache[address].Peek()]!.ChangeType == ChangeType.Touch)
            {
                return;
            }

            IncrementChangePosition();
            _intraBlockCache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(changeType, address, touchedAccount);
        }

        private void PushNew(Address address, Account account)
        {
            SetupCache(address);
            IncrementChangePosition();
            _intraBlockCache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.New, address, account);
        }

        private void IncrementChangePosition()
        {
            Resettable<Change>.IncrementPosition(ref _changes, ref _capacity, ref _currentPosition);
        }

        private void SetupCache(Address address)
        {
            if (!_intraBlockCache.ContainsKey(address))
            {
                _intraBlockCache[address] = new Stack<int>();
            }
        }

        private enum ChangeType
        {
            JustCache,
            Touch,
            Update,
            New,
            Delete
        }

        private class Change
        {
            public Change(ChangeType type, Address address, Account? account)
            {
                ChangeType = type;
                Address = address;
                Account = account;
            }

            public ChangeType ChangeType { get; }
            public Address Address { get; }
            public Account? Account { get; }
        }

        public void Reset()
        {
            if (_logger.IsTrace) _logger.Trace("Clearing state provider caches");
            _intraBlockCache.Reset();
            _committedThisRound.Reset();
            _readsForTracing.Clear();
            _currentPosition = Resettable.EmptyPosition;
            Array.Clear(_changes, 0, _changes.Length);
            _needsStateRootUpdate = false;
        }

        public void CommitTree(long blockNumber)
        {
            if (_needsStateRootUpdate)
            {
                RecalculateStateRoot();
            }

            _tree.Commit(blockNumber);
        }

        public void CommitBranch()
        {
            // placeholder for the three level Commit->CommitBlock->CommitBranch
        }

        // used in EtheereumTests
        internal void SetNonce(Address address, in UInt256 nonce)
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
    }
}
