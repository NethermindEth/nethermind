// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.State
{
    internal class StateProvider
    {
        private static readonly UInt256 _zero = UInt256.Zero;

        private readonly Dictionary<AddressAsKey, Stack<int>> _intraTxCache = new();
        private readonly HashSet<AddressAsKey> _committedThisRound = new();
        private readonly HashSet<AddressAsKey> _nullAccountReads = new();
        // Only guarding against hot duplicates so filter doesn't need to be too big
        // Note:
        // False negatives are fine as they will just result in a overwrite set
        // False positives would be problematic as the code _must_ be persisted
        private readonly ClockKeyCacheNonConcurrent<ValueHash256> _codeInsertFilter = new(1_024);
        private readonly Dictionary<AddressAsKey, ChangeTrace> _blockChanges = new(4_096);
        private readonly ConcurrentDictionary<AddressAsKey, Account>? _preBlockCache;

        private readonly List<Change> _keptInCache = new();
        private readonly ILogger _logger;
        private readonly IKeyValueStoreWithBatching _codeDb;
        private Dictionary<Hash256AsKey, byte[]> _codeBatch;
        private Dictionary<Hash256AsKey, byte[]>.AlternateLookup<ValueHash256> _codeBatchAlternate;

        private readonly List<Change> _changes = new(Resettable.StartCapacity);
        internal readonly StateTree _tree;
        private readonly Func<AddressAsKey, Account> _getStateFromTrie;

        private readonly bool _populatePreBlockCache;

        public StateProvider(IScopedTrieStore? trieStore,
            IKeyValueStoreWithBatching codeDb,
            ILogManager logManager,
            StateTree? stateTree = null,
            ConcurrentDictionary<AddressAsKey, Account>? preBlockCache = null,
            bool populatePreBlockCache = true)
        {
            _preBlockCache = preBlockCache;
            _populatePreBlockCache = populatePreBlockCache;
            _logger = logManager?.GetClassLogger<StateProvider>() ?? throw new ArgumentNullException(nameof(logManager));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _tree = stateTree ?? new StateTree(trieStore, logManager);
            _getStateFromTrie = address =>
            {
                Metrics.IncrementStateTreeReads();
                return _tree.Get(address);
            };
        }

        public void Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256? stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
        {
            ArgumentNullException.ThrowIfNull(visitor);
            ArgumentNullException.ThrowIfNull(stateRoot);

            _tree.Accept(visitor, stateRoot, visitingOptions);
        }

        private bool _needsStateRootUpdate;

        public void RecalculateStateRoot()
        {
            _tree.UpdateRootHash();
            _needsStateRootUpdate = false;
        }

        public Hash256 StateRoot
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

        public bool IsContract(Address address)
        {
            Account? account = GetThroughCache(address);
            return account is not null && account.IsContract;
        }

        public bool AccountExists(Address address) =>
            _intraTxCache.TryGetValue(address, out Stack<int> value)
                ? _changes[value.Peek()]!.ChangeType != ChangeType.Delete
                : GetAndAddToCache(address) is not null;

        public bool IsEmptyAccount(Address address)
        {
            Account? account = GetThroughCache(address);
            return account?.IsEmpty ?? throw new InvalidOperationException($"Account {address} is null when checking if empty");
        }

        public Account GetAccount(Address address) => GetThroughCache(address) ?? Account.TotallyEmpty;

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

        public Hash256 GetStorageRoot(Address address)
        {
            Account? account = GetThroughCache(address);
            return account is null ? throw new InvalidOperationException($"Account {address} is null when accessing storage root") : account.StorageRoot;
        }

        public ref readonly UInt256 GetBalance(Address address)
        {
            Account? account = GetThroughCache(address);
            return ref account is not null ? ref account.Balance : ref _zero;
        }

        public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            bool inserted = false;

            // Don't reinsert if already inserted. This can be the case when the same
            // code is used by multiple deployments. Either from factory contracts (e.g. LPs)
            // or people copy and pasting popular contracts
            if (!_codeInsertFilter.Get(codeHash))
            {
                if (_codeBatch is null)
                {
                    _codeBatch = new(Hash256AsKeyComparer.Instance);
                    _codeBatchAlternate = _codeBatch.GetAlternateLookup<ValueHash256>();
                }
                if (MemoryMarshal.TryGetArray(code, out ArraySegment<byte> codeArray)
                        && codeArray.Offset == 0
                        && codeArray.Count == code.Length)
                {
                    _codeBatchAlternate[codeHash] = codeArray.Array;
                }
                else
                {
                    _codeBatchAlternate[codeHash] = code.ToArray();
                }

                _codeInsertFilter.Set(codeHash);
                inserted = true;
            }

            Account? account = GetThroughCache(address) ?? throw new InvalidOperationException($"Account {address} is null when updating code hash");
            if (account.CodeHash.ValueHash256 != codeHash)
            {
                _needsStateRootUpdate = true;
                if (_logger.IsDebug) _logger.Debug($"  Update {address} C {account.CodeHash} -> {codeHash}");
                Account changedAccount = account.WithChangedCodeHash((Hash256)codeHash);
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

            return inserted;
        }

        private void SetNewBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSubtracting)
        {
            _needsStateRootUpdate = true;

            Account GetThroughCacheCheckExists()
            {
                Account result = GetThroughCache(address);
                if (result is null)
                {
                    if (_logger.IsDebug) _logger.Debug("Updating balance of a non-existing account");
                    throw new InvalidOperationException("Updating balance of a non-existing account");
                }

                return result;
            }

            bool isZero = balanceChange.IsZero;
            if (isZero)
            {
                // this also works like this in Geth (they don't follow the spec ¯\_(*~*)_/¯)
                // however we don't do it because of a consensus issue with Geth, just to avoid
                // hitting non-existing account when subtracting Zero-value from the sender
                if (releaseSpec.IsEip158Enabled && !isSubtracting)
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
            if (_logger.IsTrace) _logger.Trace($"  Update {address} B {account.Balance.ToHexString(skipLeadingZeros: true)} -> {newBalance.ToHexString(skipLeadingZeros: true)} ({(isSubtracting ? "-" : "+")}{balanceChange})");
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
        public void UpdateStorageRoot(Address address, Hash256 storageRoot)
        {
            _needsStateRootUpdate = true;
            Account? account = GetThroughCache(address) ?? throw new InvalidOperationException($"Account {address} is null when updating storage hash");
            if (account.StorageRoot != storageRoot)
            {
                if (_logger.IsTrace) _logger.Trace($"  Update {address} S {account.StorageRoot} -> {storageRoot}");
                Account changedAccount = account.WithChangedStorageRoot(storageRoot);
                PushUpdate(address, changedAccount);
            }
        }

        public void IncrementNonce(Address address, UInt256 delta)
        {
            _needsStateRootUpdate = true;
            Account? account = GetThroughCache(address) ?? throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
            Account changedAccount = account.WithChangedNonce(account.Nonce + delta);
            if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce.ToHexString(skipLeadingZeros: true)} -> {changedAccount.Nonce.ToHexString(skipLeadingZeros: true)}");
            PushUpdate(address, changedAccount);
        }

        public void DecrementNonce(Address address, UInt256 delta)
        {
            _needsStateRootUpdate = true;
            Account? account = GetThroughCache(address) ?? throw new InvalidOperationException($"Account {address} is null when decrementing nonce.");
            Account changedAccount = account.WithChangedNonce(account.Nonce - delta);
            if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce.ToHexString(skipLeadingZeros: true)} -> {changedAccount.Nonce.ToHexString(skipLeadingZeros: true)}");
            PushUpdate(address, changedAccount);
        }

        public ref readonly ValueHash256 GetCodeHash(Address address)
        {
            Account? account = GetThroughCache(address);
            return ref account is not null ? ref account.CodeHash.ValueHash256 : ref Keccak.OfAnEmptyString.ValueHash256;
        }

        public byte[] GetCode(in ValueHash256 codeHash)
            => GetCodeCore(in codeHash);

        private byte[] GetCodeCore(in ValueHash256 codeHash)
        {
            if (codeHash == Keccak.OfAnEmptyString.ValueHash256) return [];

            if (_codeBatch is null || !_codeBatchAlternate.TryGetValue(codeHash, out byte[]? code))
            {
                code = _codeDb[codeHash.Bytes];
            }

            return code ?? throw new InvalidOperationException($"Code {codeHash} is missing from the database.");
        }

        public byte[] GetCode(Address address)
        {
            Account? account = GetThroughCache(address);
            if (account is null)
            {
                return [];
            }

            return GetCode(in account.CodeHash.ValueHash256);
        }

        public void DeleteAccount(Address address)
        {
            _needsStateRootUpdate = true;
            PushDelete(address);
        }

        public int TakeSnapshot()
        {
            int currentPosition = _changes.Count - 1;
            if (_logger.IsTrace) _logger.Trace($"State snapshot {currentPosition}");
            return currentPosition;
        }

        public void Restore(int snapshot)
        {
            int currentPosition = _changes.Count - 1;
            if (snapshot > currentPosition)
            {
                throw new InvalidOperationException($"{nameof(StateProvider)} tried to restore snapshot {snapshot} beyond current position {currentPosition}");
            }

            if (_logger.IsTrace) _logger.Trace($"Restoring state snapshot {snapshot}");
            if (snapshot == currentPosition)
            {
                return;
            }

            for (int i = 0; i < currentPosition - snapshot; i++)
            {
                Change change = _changes[currentPosition - i];
                Stack<int> stack = _intraTxCache[change!.Address];
                if (stack.Count == 1)
                {
                    if (change.ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = stack.Pop();
                        if (actualPosition != currentPosition - i)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {currentPosition} - {i}");
                        }

                        _keptInCache.Add(change);
                        _changes[actualPosition] = default;
                        continue;
                    }
                }

                _changes[currentPosition - i] = default; // TODO: temp, ???
                int forChecking = stack.Pop();
                if (forChecking != currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forChecking} to be equal to {currentPosition} - {i}");
                }

                if (stack.Count == 0)
                {
                    _intraTxCache.Remove(change.Address);
                }
            }

            CollectionsMarshal.SetCount(_changes, snapshot + 1);
            currentPosition = _changes.Count - 1;
            foreach (Change kept in _keptInCache)
            {
                currentPosition++;
                _changes.Add(kept);
                _intraTxCache[kept.Address].Push(currentPosition);
            }

            _keptInCache.Clear();
        }

        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            _needsStateRootUpdate = true;
            if (_logger.IsTrace) _logger.Trace($"Creating account: {address} with balance {balance.ToHexString(skipLeadingZeros: true)} and nonce {nonce.ToHexString(skipLeadingZeros: true)}");
            Account account = (balance.IsZero && nonce.IsZero) ? Account.TotallyEmpty : new Account(nonce, balance);
            PushNew(address, account);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            if (!AccountExists(address))
            {
                CreateAccount(address, balance, nonce);
            }
        }

        public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balance, IReleaseSpec spec)
        {
            if (AccountExists(address))
            {
                AddToBalance(address, balance, spec);
                return false;
            }
            else
            {
                CreateAccount(address, balance);
                return true;
            }
        }

        public void Commit(IReleaseSpec releaseSpec, bool commitRoots, bool isGenesis)
        {
            Commit(releaseSpec, NullStateTracer.Instance, commitRoots, isGenesis);
        }

        private struct ChangeTrace(Account? before, Account? after)
        {
            public ChangeTrace(Account? after) : this(null, after)
            {
            }

            public Account? Before { get; set; } = before;
            public Account? After { get; set; } = after;
        }

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer stateTracer, bool commitRoots, bool isGenesis)
        {
            Task codeFlushTask = !commitRoots || _codeBatch is null || _codeBatch.Count == 0
                ? Task.CompletedTask
                : CommitCodeAsync();

            var currentPosition = _changes.Count - 1;
            if (currentPosition < 0)
            {
                if (_logger.IsTrace) _logger.Trace("  no state changes to commit");
                if (commitRoots)
                {
                    FlushToTree();
                }
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Committing state changes (at {currentPosition})");
            if (_changes[currentPosition].IsNull)
            {
                throw new InvalidOperationException($"Change at current position {currentPosition} was null when committing {nameof(StateProvider)}");
            }

            bool isTracing = stateTracer.IsTracingState;
            Dictionary<AddressAsKey, ChangeTrace> trace = null;
            if (isTracing)
            {
                trace = new Dictionary<AddressAsKey, ChangeTrace>();
            }

            for (int i = 0; i <= currentPosition; i++)
            {
                Change change = _changes[currentPosition - i];
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
                    _nullAccountReads.Add(change.Address);
                    continue;
                }

                Stack<int> stack = _intraTxCache[change.Address];
                int forAssertion = stack.Pop();
                if (forAssertion != currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {currentPosition} - {i}");
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
                                if (_logger.IsTrace) _logger.Trace($"  Commit remove empty {change.Address} B = {change.Account.Balance.ToHexString(skipLeadingZeros: true)} N = {change.Account.Nonce.ToHexString(skipLeadingZeros: true)}");
                                SetState(change.Address, null);
                                if (isTracing)
                                {
                                    trace[change.Address] = new ChangeTrace(null);
                                }
                            }
                            else
                            {
                                if (_logger.IsTrace) _logger.Trace($"  Commit update {change.Address} B = {change.Account.Balance.ToHexString(skipLeadingZeros: true)} N = {change.Account.Nonce.ToHexString(skipLeadingZeros: true)} C = {change.Account.CodeHash}");
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
                                if (_logger.IsTrace) _logger.Trace($"  Commit create {change.Address} B = {change.Account.Balance.ToHexString(skipLeadingZeros: true)} N = {change.Account.Nonce.ToHexString(skipLeadingZeros: true)}");
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
                            while (stack.Count > 0)
                            {
                                int previousOne = stack.Pop();
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
                foreach (Address nullRead in _nullAccountReads)
                {
                    // // this may be enough, let us write tests
                    stateTracer.ReportAccountRead(nullRead);
                }
            }

            _changes.Clear();
            _committedThisRound.Clear();
            _nullAccountReads.Clear();
            _intraTxCache.Clear();

            if (isTracing)
            {
                ReportChanges(stateTracer, trace);
            }

            if (commitRoots)
            {
                FlushToTree();
            }

            codeFlushTask.GetAwaiter().GetResult();

            Task CommitCodeAsync()
            {
                Dictionary<Hash256AsKey, byte[]> dict = Interlocked.Exchange(ref _codeBatch, null);
                if (dict is null) return Task.CompletedTask;
                _codeBatchAlternate = default;

                return Task.Run(() =>
                {
                    using (var batch = _codeDb.StartWriteBatch())
                    {
                        // Insert ordered for improved performance
                        foreach (var kvp in dict.OrderBy(static kvp => kvp.Key))
                        {
                            batch.PutSpan(kvp.Key.Value.Bytes, kvp.Value);
                        }
                    }

                    // Reuse Dictionary if not already re-initialized
                    dict.Clear();
                    if (Interlocked.CompareExchange(ref _codeBatch, dict, null) is null)
                    {
                        _codeBatchAlternate = _codeBatch.GetAlternateLookup<ValueHash256>();
                    }
                });
            }
        }

        private void FlushToTree()
        {
            int writes = 0;
            int skipped = 0;
            foreach (var key in _blockChanges.Keys)
            {
                ref var change = ref CollectionsMarshal.GetValueRefOrNullRef(_blockChanges, key);
                if (change.Before != change.After)
                {
                    change.Before = change.After;
                    _tree.Set(key, change.After);
                    writes++;
                }
                else
                {
                    skipped++;
                }
            }

            if (writes > 0)
                Metrics.IncrementStateTreeWrites(writes);
            if (skipped > 0)
                Metrics.IncrementStateSkippedWrites(skipped);
        }

        private void ReportChanges(IStateTracer stateTracer, Dictionary<AddressAsKey, ChangeTrace> trace)
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

                Hash256? beforeCodeHash = before?.CodeHash;
                Hash256? afterCodeHash = after?.CodeHash;

                if (beforeCodeHash != afterCodeHash)
                {
                    byte[]? beforeCode = beforeCodeHash is null
                        ? null
                        : beforeCodeHash == Keccak.OfAnEmptyString
                            ? []
                            : GetCodeCore(in beforeCodeHash.ValueHash256);
                    byte[]? afterCode = afterCodeHash is null
                        ? null
                        : afterCodeHash == Keccak.OfAnEmptyString
                            ? []
                            : GetCodeCore(in afterCodeHash.ValueHash256);

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

        public bool WarmUp(Address address)
        {
            return GetState(address) is not null;
        }

        private Account? GetState(Address address)
        {
            AddressAsKey addressAsKey = address;
            ref ChangeTrace accountChanges = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockChanges, addressAsKey, out bool exists);
            if (!exists)
            {
                Account? account = !_populatePreBlockCache ?
                    GetStateReadPreWarmCache(addressAsKey) :
                    GetStatePopulatePrewarmCache(addressAsKey);

                accountChanges = new(account, account);
            }
            else
            {
                Metrics.IncrementStateTreeCacheHits();
            }
            return accountChanges.After;
        }

        private Account? GetStatePopulatePrewarmCache(AddressAsKey addressAsKey)
        {
            long priorReads = Metrics.ThreadLocalStateTreeReads;
            Account? account = _preBlockCache is not null
                ? _preBlockCache.GetOrAdd(addressAsKey, _getStateFromTrie)
                : _getStateFromTrie(addressAsKey);

            if (Metrics.ThreadLocalStateTreeReads == priorReads)
            {
                Metrics.IncrementStateTreeCacheHits();
            }
            return account;
        }

        private Account? GetStateReadPreWarmCache(AddressAsKey addressAsKey)
        {
            if (_preBlockCache?.TryGetValue(addressAsKey, out Account? account) ?? false)
            {
                Metrics.IncrementStateTreeCacheHits();
            }
            else
            {
                account = _getStateFromTrie(addressAsKey);
            }
            return account;
        }

        private void SetState(Address address, Account? account)
        {
            ref ChangeTrace accountChanges = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockChanges, address, out _);
            accountChanges.After = account;
            _needsStateRootUpdate = true;
        }

        private Account? GetAndAddToCache(Address address)
        {
            if (_nullAccountReads.Contains(address)) return null;

            Account? account = GetState(address);
            if (account is not null)
            {
                PushJustCache(address, account);
            }
            else
            {
                // just for tracing - potential perf hit, maybe a better solution?
                _nullAccountReads.Add(address);
            }

            return account;
        }

        private Account? GetThroughCache(Address address)
        {
            if (_intraTxCache.TryGetValue(address, out Stack<int> value))
            {
                return _changes[value.Peek()].Account;
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
            Stack<int> stack = SetupCache(address);
            if (changeType == ChangeType.Touch
                && _changes[stack.Peek()]!.ChangeType == ChangeType.Touch)
            {
                return;
            }

            stack.Push(_changes.Count);
            _changes.Add(new Change(changeType, address, touchedAccount));
        }

        private void PushNew(Address address, Account account)
        {
            Stack<int> stack = SetupCache(address);
            stack.Push(_changes.Count);
            _changes.Add(new Change(ChangeType.New, address, account));
        }

        private Stack<int> SetupCache(Address address)
        {
            ref Stack<int>? value = ref CollectionsMarshal.GetValueRefOrAddDefault(_intraTxCache, address, out bool exists);
            if (!exists)
            {
                value = new Stack<int>();
            }

            return value;
        }

        private enum ChangeType
        {
            Null = 0,
            JustCache,
            Touch,
            Update,
            New,
            Delete
        }

        private readonly struct Change
        {
            public Change(ChangeType type, Address address, Account? account)
            {
                ChangeType = type;
                Address = address;
                Account = account;
            }

            public readonly ChangeType ChangeType;
            public readonly Address Address;
            public readonly Account? Account;

            public bool IsNull => ChangeType == ChangeType.Null;
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

        public void Reset(bool resetBlockChanges = true)
        {
            if (_logger.IsTrace) _logger.Trace("Clearing state provider caches");
            if (resetBlockChanges)
            {
                _blockChanges.Clear();
                _codeBatch?.Clear();
            }
            _intraTxCache.Clear();
            _committedThisRound.Clear();
            _nullAccountReads.Clear();
            _changes.Clear();
            _needsStateRootUpdate = false;
        }

        public void CommitTree()
        {
            if (_needsStateRootUpdate)
            {
                RecalculateStateRoot();
            }

            _tree.Commit();
        }

        // used in EthereumTests
        internal void SetNonce(Address address, in UInt256 nonce)
        {
            _needsStateRootUpdate = true;
            Account? account = GetThroughCache(address) ?? throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
            Account changedAccount = account.WithChangedNonce(nonce);
            if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce} -> {changedAccount.Nonce}");
            PushUpdate(address, changedAccount);
        }
    }
}
