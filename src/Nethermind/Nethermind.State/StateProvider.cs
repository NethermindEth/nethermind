// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
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
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Metrics = Nethermind.Db.Metrics;
using static Nethermind.State.StateProvider;

namespace Nethermind.State
{
    internal class StateProvider
    {
        private static readonly UInt256 _zero = UInt256.Zero;

        private static readonly AccountStruct? NullAccountStruct = null; // Cannot put in AccountStruct for some reason.

        private readonly Dictionary<AddressAsKey, Stack<int>> _intraTxCache = new();
        private readonly HashSet<AddressAsKey> _committedThisRound = new();
        private readonly HashSet<AddressAsKey> _nullAccountReads = new();
        // Only guarding against hot duplicates so filter doesn't need to be too big
        // Note:
        // False negatives are fine as they will just result in a overwrite set
        // False positives would be problematic as the code _must_ be persisted
        private readonly ClockKeyCacheNonConcurrent<ValueHash256> _codeInsertFilter = new(1_024);
        private readonly Dictionary<AddressAsKey, ChangeTrace> _blockChanges = new(4_096);
        private readonly ConcurrentDictionary<AddressAsKey, AccountStruct?>? _preBlockCache;

        private readonly List<Change> _keptInCache = new();
        private readonly ILogger _logger;
        private readonly IKeyValueStoreWithBatching _codeDb;
        private Dictionary<Hash256AsKey, byte[]> _codeBatch;
        private Dictionary<Hash256AsKey, byte[]>.AlternateLookup<ValueHash256> _codeBatchAlternate;

        private readonly List<Change> _changes = new(Resettable.StartCapacity);
        internal readonly StateTree _tree;
        private readonly Func<AddressAsKey, AccountStruct?> _getStateFromTrie;

        private readonly bool _populatePreBlockCache;
        private bool _needsStateRootUpdate;

        public StateProvider(IScopedTrieStore? trieStore,
            IKeyValueStoreWithBatching codeDb,
            ILogManager logManager,
            StateTree? stateTree = null,
            ConcurrentDictionary<AddressAsKey, AccountStruct?>? preBlockCache = null,
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
                return _tree.GetStruct(address);
            };
        }

        public void RecalculateStateRoot()
        {
            _tree.UpdateRootHash();
            _needsStateRootUpdate = false;
        }

        public Hash256 StateRoot
        {
            get
            {
                if (_needsStateRootUpdate) ThrowStateRootNeedsToBeUpdated();
                return _tree.RootHash;

                [DoesNotReturn, StackTraceHidden]
                static void ThrowStateRootNeedsToBeUpdated() => throw new InvalidOperationException("State root needs to be updated");
            }
            set => _tree.RootHash = value;
        }

        public bool IsContract(Address address)
        {
            ref readonly AccountStruct? account = ref GetThroughCache(address);
            return account is not null && account.Value.IsContract;
        }

        public bool AccountExists(Address address) =>
            _intraTxCache.TryGetValue(address, out Stack<int> value)
                ? _changes[value.Peek()]!.ChangeType != ChangeType.Delete
                : GetAndAddToCache(address) is not null;

        public ref readonly AccountStruct? GetAccount(Address address)
        {
            return ref GetThroughCache(address);
        }

        public bool IsDeadAccount(Address address)
        {
            ref readonly AccountStruct? account = ref GetThroughCache(address);
            return account?.IsEmpty ?? true;
        }

        public UInt256 GetNonce(Address address)
        {
            ref readonly AccountStruct? account = ref GetThroughCache(address);
            return account?.Nonce ?? UInt256.Zero;
        }

        public Hash256 GetStorageRoot(Address address) // TODO: Check if ValueHash
        {
            ref readonly AccountStruct? account = ref GetThroughCache(address);
            return account is not null ? account.Value.StorageRoot.ToCommitment() : ThrowIfNull(address);

            [DoesNotReturn, StackTraceHidden]
            static Hash256 ThrowIfNull(Address address)
                => throw new InvalidOperationException($"Account {address} is null when accessing storage root");
        }

        public ref readonly UInt256 GetBalance(Address address)
        {
            ref readonly AccountStruct? account = ref GetThroughCache(address);
            return ref account is not null ? ref Nullable.GetValueRefOrDefaultRef(in account).Balance : ref _zero;
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

            ref readonly AccountStruct account = ref GetThroughCacheOrThrowIfNull(address);
            if (account.CodeHash != codeHash)
            {
                _needsStateRootUpdate = true;
                if (_logger.IsDebug) Debug(address, codeHash, account);
                AccountStruct changedAccount = account.WithChangedCodeHash((Hash256)codeHash);

                PushUpdate(address, changedAccount);
            }
            else if (spec.IsEip158Enabled && !isGenesis)
            {
                if (_logger.IsTrace) Trace(address);
                if (account.IsEmpty)
                {
                    PushTouch(address, account, spec, account.Balance.IsZero);
                }
            }

            return inserted;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Debug(Address address, in ValueHash256 codeHash, in AccountStruct account)
                => _logger.Debug($"Update {address} C {account.CodeHash} -> {codeHash}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address) => _logger.Trace($"Touch {address} (code hash)");
        }

        private void SetNewBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSubtracting)
        {
            _needsStateRootUpdate = true;

            AccountStruct GetThroughCacheCheckExists()
            {
                ref readonly AccountStruct? result = ref GetThroughCache(address);
                if (result is null)
                {
                    ThrowNonExistingAccount();
                }

                return result.Value;

                [DoesNotReturn, StackTraceHidden]
                static void ThrowNonExistingAccount()
                    => throw new InvalidOperationException("Updating balance of a non-existing account");
            }

            bool isZero = balanceChange.IsZero;
            if (isZero)
            {
                // this also works like this in Geth (they don't follow the spec ¯\_(*~*)_/¯)
                // however we don't do it because of a consensus issue with Geth, just to avoid
                // hitting non-existing account when subtracting Zero-value from the sender
                if (releaseSpec.IsEip158Enabled && !isSubtracting)
                {
                    AccountStruct touched = GetThroughCacheCheckExists();

                    if (_logger.IsTrace) TraceTouch(address);
                    if (touched.IsEmpty)
                    {
                        PushTouch(address, touched, releaseSpec, true);
                    }
                }

                return;
            }

            AccountStruct account = GetThroughCacheCheckExists();

            if (isSubtracting && account.Balance < balanceChange)
            {
                ThrowInsufficientBalanceException(address);
            }

            UInt256 newBalance = isSubtracting ? account.Balance - balanceChange : account.Balance + balanceChange;

            AccountStruct changedAccount = account.WithChangedBalance(newBalance);
            if (_logger.IsTrace) TraceUpdate(address, in balanceChange, isSubtracting, account, in newBalance);

            PushUpdate(address, changedAccount);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceTouch(Address address) => _logger.Trace($"Touch {address} (balance)");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceUpdate(Address address, in UInt256 balanceChange, bool isSubtracting, in AccountStruct account, in UInt256 newBalance)
                => _logger.Trace($"Update {address} B {account.Balance.ToHexString(skipLeadingZeros: true)} -> {newBalance.ToHexString(skipLeadingZeros: true)} ({(isSubtracting ? "-" : "+")}{balanceChange})");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowInsufficientBalanceException(Address address)
                => throw new InsufficientBalanceException(address);
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
            ref readonly AccountStruct account = ref GetThroughCacheOrThrowIfNull(address);
            if (account.StorageRoot != storageRoot)
            {
                if (_logger.IsTrace) Trace(address, storageRoot, account);
                AccountStruct changedAccount = account.WithChangedStorageRoot(storageRoot);
                PushUpdate(address, changedAccount);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, in AccountStruct account)
                => _logger.Trace($"Update {address} S {account.StorageRoot} -> {storageRoot}");
        }

        public void IncrementNonce(Address address, UInt256 delta)
        {
            _needsStateRootUpdate = true;
            ref readonly AccountStruct account = ref GetThroughCacheOrThrowIfNull(address);
            AccountStruct changedAccount = account.WithChangedNonce(account.Nonce + delta);
            if (_logger.IsTrace) Trace(address, account, changedAccount);

            PushUpdate(address, changedAccount);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, in AccountStruct account, in AccountStruct changedAccount)
                => _logger.Trace($"Update {address} N {account.Nonce.ToHexString(skipLeadingZeros: true)} -> {changedAccount.Nonce.ToHexString(skipLeadingZeros: true)}");
        }

        public void DecrementNonce(Address address, UInt256 delta)
        {
            _needsStateRootUpdate = true;
            ref readonly AccountStruct account = ref GetThroughCacheOrThrowIfNull(address);
            AccountStruct changedAccount = account.WithChangedNonce(account.Nonce - delta);
            if (_logger.IsTrace) Trace(address, account, changedAccount);

            PushUpdate(address, changedAccount);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, in AccountStruct account, in AccountStruct changedAccount)
                => _logger.Trace($"  Update {address} N {account.Nonce.ToHexString(skipLeadingZeros: true)} -> {changedAccount.Nonce.ToHexString(skipLeadingZeros: true)}");
        }

        public ref readonly ValueHash256 GetCodeHash(Address address)
        {
            ref readonly AccountStruct? account = ref GetThroughCache(address);
            return ref account is not null ? ref Nullable.GetValueRefOrDefaultRef(in account).CodeHash : ref Keccak.OfAnEmptyString.ValueHash256;
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
            return code ?? ThrowMissingCode(in codeHash);

            [DoesNotReturn, StackTraceHidden]
            static byte[] ThrowMissingCode(in ValueHash256 codeHash)
                => throw new InvalidOperationException($"Code {codeHash} is missing from the database.");
        }

        public byte[] GetCode(Address address)
        {
            ref readonly AccountStruct? account = ref GetThroughCache(address);
            if (account is null)
            {
                return [];
            }

            return GetCode(account.Value!.CodeHash);
        }

        public void DeleteAccount(Address address)
        {
            _needsStateRootUpdate = true;
            PushDelete(address);
        }

        public int TakeSnapshot()
        {
            int currentPosition = _changes.Count - 1;
            if (_logger.IsTrace) Trace(currentPosition);

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
            // No-op if already at the desired snapshot
            if (snapshot == lastIndex) return;

            int stepsBack = lastIndex - snapshot;
            // Reserve capacity up‐front (avoid grows)
            if (_keptInCache.Capacity < stepsBack)
                _keptInCache.Capacity = stepsBack;

            ReadOnlySpan<Change> changes = CollectionsMarshal.AsSpan(_changes);
            // Roll back each change from newest down to target
            for (int i = 0; i < stepsBack; i++)
            {
                int nextPosition = lastIndex - i;
                ref readonly Change change = ref changes[nextPosition];
                Stack<int> stack = _intraTxCache[change!.Address];

                int actualPosition = stack.Pop();
                if (actualPosition != nextPosition) ThrowUnexpectedPosition(lastIndex, i, actualPosition);

                if (stack.Count == 0)
                {
                    if (change.ChangeType == ChangeType.JustCache)
                    {
                        // Keep if was caching entry
                        _keptInCache.Add(change);
                    }
                    else
                    {
                        // Remove address entry entirely if no more changes
                        _intraTxCache.Remove(change.Address);
                    }
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
                _intraTxCache[kept.Address].Push(snapshot);
            }
            _keptInCache.Clear();

            // Local helpers to keep cold code from throws and string interpolation out of hot code.
            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(int snap) => _logger.Trace($"Restoring state snapshot {snap}");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowCannotRestore(int current, int snap)
                => throw new InvalidOperationException($"{nameof(StateProvider)} tried to restore snapshot {snap} beyond current position {current}");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedPosition(int current, int step, int actual)
                => throw new InvalidOperationException($"Expected actual position {actual} to be equal to {current} - {step}");
        }

        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            _needsStateRootUpdate = true;
            if (_logger.IsTrace) Trace(address, balance, nonce);

            AccountStruct account = (balance.IsZero && nonce.IsZero) ? AccountStruct.TotallyEmpty : new AccountStruct(nonce, balance);
            PushNew(address, account);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, in UInt256 balance, in UInt256 nonce)
                => _logger.Trace($"Creating account: {address} with balance {balance.ToHexString(skipLeadingZeros: true)} and nonce {nonce.ToHexString(skipLeadingZeros: true)}");
        }

        public void CreateEmptyAccountIfDeletedOrNew(Address address)
        {
            if (_intraTxCache.TryGetValue(address, out Stack<int> value))
            {
                //we only want to persist empty accounts if they were deleted or created as empty
                //we don't want to do it for account empty due to a change (e.g. changed balance to zero)
                var lastChange = _changes[value.Peek()];
                if (lastChange.ChangeType == ChangeType.Delete ||
                    (lastChange.ChangeType is ChangeType.Touch or ChangeType.New && lastChange.Account.IsEmpty))
                {
                    _needsStateRootUpdate = true;
                    if (_logger.IsTrace) Trace(address);

                    Account account = Account.TotallyEmpty;
                    PushRecreateEmpty(address, account, value);
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address)
                => _logger.Trace($"Creating zombie account: {address}");
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
            => Commit(releaseSpec, NullStateTracer.Instance, commitRoots, isGenesis);

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer stateTracer, bool commitRoots, bool isGenesis)
        {
            Task codeFlushTask = !commitRoots || _codeBatch is null || _codeBatch.Count == 0
                ? Task.CompletedTask
                : CommitCodeAsync();

            bool isTracing = _logger.IsTrace;
            int stepsBack = _changes.Count - 1;
            if (stepsBack < 0)
            {
                if (isTracing) TraceNoChanges();
                if (commitRoots)
                {
                    FlushToTree();
                }
                return;
            }

            if (isTracing) TraceCommit(stepsBack);
            if (_changes[stepsBack].IsNull)
            {
                ThrowStartOfCommitIsNull(stepsBack);
            }

            Dictionary<AddressAsKey, ChangeTrace>? trace = !stateTracer.IsTracingState ? null : [];

            ReadOnlySpan<Change> changes = CollectionsMarshal.AsSpan(_changes);
            for (int i = 0; i <= stepsBack; i++)
            {
                ref readonly Change change = ref changes[stepsBack - i];
                if (trace is null && change!.ChangeType == ChangeType.JustCache)
                {
                    continue;
                }

                if (_committedThisRound.Contains(change!.Address))
                {
                    if (change.ChangeType == ChangeType.JustCache)
                    {
                        trace?.UpdateTrace(change.Address, change.Account);
                    }

                    continue;
                }

                // because it was not committed yet it means that the just cache is the only state (so it was read only)
                if (trace is not null && change.ChangeType == ChangeType.JustCache)
                {
                    _nullAccountReads.Add(change.Address);
                    continue;
                }

                Stack<int> stack = _intraTxCache[change.Address];
                int forAssertion = stack.Pop();
                if (forAssertion != stepsBack - i)
                {
                    ThrowUnexpectedPosition(stepsBack, i, forAssertion);
                }

                _committedThisRound.Add(change.Address);

                switch (change.ChangeType)
                {
                    case ChangeType.JustCache:
                        break;
                    case ChangeType.Touch:
                    case ChangeType.Update:
                        {
                            if (releaseSpec.IsEip158Enabled && change.Account.Value!.IsEmpty && !isGenesis)
                            {
                                if (isTracing) TraceRemoveEmpty(change);
                                SetState(change.Address, null);
                                trace?.AddToTrace(change.Address, null);
                            }
                            else
                            {
                                if (isTracing) TraceUpdate(change);
                                SetState(change.Address, change.Account);
                                trace?.AddToTrace(change.Address, change.Account);
                            }

                            break;
                        }
                    case ChangeType.New:
                        {
                            if (!releaseSpec.IsEip158Enabled || !change.Account.Value!.IsEmpty || isGenesis)
                            {
                                if (isTracing) TraceCreate(change);
                                SetState(change.Address, change.Account);
                                trace?.AddToTrace(change.Address, change.Account);
                            }

                            break;
                        }
                    case ChangeType.RecreateEmpty:
                        {
                            if (isTracing) TraceCreate(change);
                            SetState(change.Address, change.Account);
                            trace?.AddToTrace(change.Address, change.Account);

                            break;
                        }
                    case ChangeType.Delete:
                        {
                            if (isTracing) TraceRemove(change);
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
                                trace?.AddToTrace(change.Address, null);
                            }

                            break;
                        }
                    default:
                        ThrowUnknownChangeType();
                        break;
                }
            }

            trace?.ReportStateTrace(stateTracer, _nullAccountReads, this);

            _changes.Clear();
            _committedThisRound.Clear();
            _nullAccountReads.Clear();
            _intraTxCache.Clear();

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

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCommit(int currentPosition) => _logger.Trace($"Committing state changes (at {currentPosition})");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceNoChanges() => _logger.Trace("No state changes to commit");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceRemove(in Change change) => _logger.Trace($"Commit remove {change.Address}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCreate(in Change change)
                => _logger.Trace($"Commit create {change.Address} B = {change.Account?.Balance.ToHexString(skipLeadingZeros: true)} N = {change.Account?.Nonce.ToHexString(skipLeadingZeros: true)}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceUpdate(in Change change)
                => _logger.Trace($"Commit update {change.Address} B = {change.Account?.Balance.ToHexString(skipLeadingZeros: true)} N = {change.Account?.Nonce.ToHexString(skipLeadingZeros: true)} C = {change.Account?.CodeHash}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceRemoveEmpty(in Change change)
                => _logger.Trace($"Commit remove empty {change.Address} B = {change.Account?.Balance.ToHexString(skipLeadingZeros: true)} N = {change.Account?.Nonce.ToHexString(skipLeadingZeros: true)}");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowStartOfCommitIsNull(int currentPosition)
                => throw new InvalidOperationException($"Change at current position {currentPosition} was null when committing {nameof(StateProvider)}");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnknownChangeType() => throw new ArgumentOutOfRangeException();

            [DoesNotReturn, StackTraceHidden]
            static void ThrowUnexpectedPosition(int currentPosition, int i, int forAssertion)
                => throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {currentPosition} - {i}");
        }

        private void FlushToTree()
        {
            int writes = 0;
            int skipped = 0;

            using ArrayPoolList<PatriciaTree.BulkSetEntry> bulkWrite = new(_blockChanges.Count);
            foreach (var key in _blockChanges.Keys)
            {
                ref var change = ref CollectionsMarshal.GetValueRefOrNullRef(_blockChanges, key);
                if (change.Before != change.After)
                {
                    change.Before = change.After;
                    KeccakCache.ComputeTo(key.Value.Bytes, out ValueHash256 keccak);

                    var account = change.After;
                    Rlp accountRlp = account is not {} notNullAccount ? null : notNullAccount.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Rlp.Encode(account);

                    bulkWrite.Add(new PatriciaTree.BulkSetEntry(keccak, accountRlp?.Bytes));
                    writes++;
                }
                else
                {
                    skipped++;
                }
            }

            _tree.BulkSet(bulkWrite);

            if (writes > 0)
                Metrics.IncrementStateTreeWrites(writes);
            if (skipped > 0)
                Metrics.IncrementStateSkippedWrites(skipped);
        }

        public bool WarmUp(Address address)
            => GetState(address) is not null;

        private ref AccountStruct? GetState(Address address)
        {
            AddressAsKey addressAsKey = address;
            ref ChangeTrace accountChanges = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockChanges, addressAsKey, out bool exists);
            if (!exists)
            {
                AccountStruct? account = !_populatePreBlockCache ?
                    GetStateReadPreWarmCache(addressAsKey) :
                    GetStatePopulatePrewarmCache(addressAsKey);

                accountChanges = new(account, account);
            }
            else
            {
                Metrics.IncrementStateTreeCacheHits();
            }
            return ref accountChanges.After;
        }

        private AccountStruct? GetStatePopulatePrewarmCache(AddressAsKey addressAsKey)
        {
            long priorReads = Metrics.ThreadLocalStateTreeReads;
            AccountStruct? account = _preBlockCache is not null
                ? _preBlockCache.GetOrAdd(addressAsKey, _getStateFromTrie)
                : _getStateFromTrie(addressAsKey);

            if (Metrics.ThreadLocalStateTreeReads == priorReads)
            {
                Metrics.IncrementStateTreeCacheHits();
            }
            return account;
        }

        private AccountStruct? GetStateReadPreWarmCache(AddressAsKey addressAsKey)
        {
            if (_preBlockCache?.TryGetValue(addressAsKey, out AccountStruct? account) ?? false)
            {
                Metrics.IncrementStateTreeCacheHits();
            }
            else
            {
                account = _getStateFromTrie(addressAsKey);
            }
            return account;
        }

        private void SetState(Address address, AccountStruct? account)
        {
            ref ChangeTrace accountChanges = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockChanges, address, out _);
            accountChanges.After = account;
            _needsStateRootUpdate = true;
        }

        private ref readonly AccountStruct? GetAndAddToCache(Address address)
        {
            if (_nullAccountReads.Contains(address)) return ref NullAccountStruct;

            ref AccountStruct? account = ref GetState(address);
            if (account is not null)
            {
                PushJustCache(address, account.Value);
            }
            else
            {
                // just for tracing - potential perf hit, maybe a better solution?
                _nullAccountReads.Add(address);
            }

            return ref account;
        }

        // TODO: Return readonly ref
        private ref readonly AccountStruct? GetThroughCache(Address address)
        {
            if (_intraTxCache.TryGetValue(address, out Stack<int> value))
            {
                return ref  CollectionsMarshal.AsSpan(_changes)[value.Peek()].Account;
            }

            ref readonly AccountStruct? account = ref GetAndAddToCache(address);
            return ref account;
        }


        private ref readonly AccountStruct GetThroughCacheOrThrowIfNull(Address address)
        {
            ref readonly AccountStruct? account = ref GetThroughCache(address);
            if (account.HasValue)
            {
                return ref Nullable.GetValueRefOrDefaultRef(in account);
            }

            return ref ThrowIfNull(address);

            static ref readonly AccountStruct ThrowIfNull(Address address)
                => throw new InvalidOperationException($"Account {address} is null when accessing storage root");
        }

        private void PushJustCache(Address address, AccountStruct account)
            => Push(address, account, ChangeType.JustCache);

        private void PushUpdate(Address address, AccountStruct account)
            => Push(address, account, ChangeType.Update);

        private void PushTouch(Address address, AccountStruct account, IReleaseSpec releaseSpec, bool isZero)
        {
            if (isZero && releaseSpec.IsEip158IgnoredAccount(address)) return;
            Push(address, account, ChangeType.Touch);
        }

        private void PushDelete(Address address)
            => Push(address, null, ChangeType.Delete);

        private void Push(Address address, AccountStruct? touchedAccount, ChangeType changeType)
        {
            Stack<int> stack = SetupCache(address);
            if (changeType == ChangeType.Touch
                && _changes[stack.Peek()]!.ChangeType == ChangeType.Touch)
            {
                return;
            }

            stack.Push(_changes.Count);
            _changes.Add(new Change(address, touchedAccount, changeType));
        }

        private void PushNew(Address address, AccountStruct account)
        {
            Stack<int> stack = SetupCache(address);
            stack.Push(_changes.Count);
            _changes.Add(new Change(address, account, ChangeType.New));
        }

        private void PushRecreateEmpty(Address address, Account account, Stack<int> stack)
        {
            stack.Push(_changes.Count);
            _changes.Add(new Change(address, account, ChangeType.RecreateEmpty));
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
            if (_logger.IsTrace) Trace();
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

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace() => _logger.Trace("Clearing state provider caches");
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
            AccountStruct account = GetThroughCache(address) ?? ThrowNullAccount(address);
            AccountStruct changedAccount = account.WithChangedNonce(nonce);
            if (_logger.IsTrace) Trace(address, account, changedAccount);

            PushUpdate(address, changedAccount);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, AccountStruct account, AccountStruct changedAccount)
                => _logger.Trace($"Update {address} N {account.Nonce} -> {changedAccount.Nonce}");

            [DoesNotReturn, StackTraceHidden]
            static AccountStruct ThrowNullAccount(Address address)
                => throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
        }

        private enum ChangeType
        {
            Null = 0,
            JustCache,
            Touch,
            Update,
            New,
            Delete,
            RecreateEmpty,
        }

        private readonly struct Change(Address address, AccountStruct? account, ChangeType type)
        {
            public readonly Address Address = address;
            public readonly AccountStruct? Account = account;
            public readonly ChangeType ChangeType = type;

            public bool IsNull => ChangeType == ChangeType.Null;
        }

        internal struct ChangeTrace(AccountStruct? before, AccountStruct? after)
        {
            public ChangeTrace(AccountStruct? after) : this(null, after)
            {
            }

            public AccountStruct? Before = before;
            public AccountStruct? After = after;
        }
    }

    internal static class Extensions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void AddToTrace(this Dictionary<AddressAsKey, ChangeTrace> trace, Address address, AccountStruct? change)
        {
            trace.Add(address, new ChangeTrace(change));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void UpdateTrace(this Dictionary<AddressAsKey, ChangeTrace> trace, Address address, AccountStruct? change)
        {
            trace[address] = new ChangeTrace(change, trace[address].After);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReportStateTrace(this Dictionary<AddressAsKey, ChangeTrace>? trace, IWorldStateTracer stateTracer, HashSet<AddressAsKey> nullAccountReads, StateProvider stateProvider)
        {
            foreach (Address nullRead in nullAccountReads)
            {
                // // this may be enough, let us write tests
                stateTracer.ReportAccountRead(nullRead);
            }
            ReportChanges(trace, stateTracer, stateProvider);
        }

        private static void ReportChanges(Dictionary<AddressAsKey, ChangeTrace> trace, IStateTracer stateTracer, StateProvider stateProvider)
        {
            foreach ((Address address, ChangeTrace change) in trace)
            {
                bool someChangeReported = false;

                AccountStruct? before = change.Before;
                AccountStruct? after = change.After;

                UInt256? beforeBalance = before?.Balance;
                UInt256? afterBalance = after?.Balance;

                UInt256? beforeNonce = before?.Nonce;
                UInt256? afterNonce = after?.Nonce;

                ValueHash256? beforeCodeHash = before?.CodeHash;
                ValueHash256? afterCodeHash = after?.CodeHash;

                if (beforeCodeHash != afterCodeHash)
                {
                    byte[]? beforeCode = beforeCodeHash is null
                        ? null
                        : beforeCodeHash == Keccak.OfAnEmptyString
                            ? []
                            : stateProvider.GetCode(beforeCodeHash.Value);
                    byte[]? afterCode = afterCodeHash is null
                        ? null
                        : afterCodeHash == Keccak.OfAnEmptyString
                            ? []
                            : stateProvider.GetCode(afterCodeHash.Value);

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
    }
}
