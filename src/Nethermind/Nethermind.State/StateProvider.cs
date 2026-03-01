// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Metrics = Nethermind.Db.Metrics;
using static Nethermind.State.StateProvider;

namespace Nethermind.State
{
    internal class StateProvider
    {
        private static readonly UInt256 _zero = UInt256.Zero;

        private readonly Dictionary<AddressAsKey, int> _slotIndex = new();
        private (Address? Key, int SlotIndex) _inlineCache0;
        private (Address? Key, int SlotIndex) _inlineCache1;
        private (Address? Key, int SlotIndex) _inlineCache2;
        private (Address? Key, int SlotIndex) _inlineCache3;
        private bool[] _exists = new bool[Resettable.StartCapacity];
        private UInt256[] _balances = new UInt256[Resettable.StartCapacity];
        private UInt256[] _nonces = new UInt256[Resettable.StartCapacity];
        private Hash256?[] _codeHashes = new Hash256?[Resettable.StartCapacity];
        private bool[] _hasCode = new bool[Resettable.StartCapacity];
        private SlotMeta[] _slotsMeta = new SlotMeta[Resettable.StartCapacity];
        private int _slotCount;
        private UndoEntry[] _undoBuffer = new UndoEntry[64];
        private int _undoCount;
        private int[] _frameUndoOffsets = new int[32];
        private int[] _frameIds = new int[32];
        private int _frameCount = 1;
        private int _currentFrameId;
        private int[] _dirtySlotIndices = new int[Resettable.StartCapacity];
        private int _dirtyCount;
        private int _dirtyGeneration = 1;
        // Only guarding against hot duplicates so filter doesn't need to be too big
        // Note:
        // False negatives are fine as they will just result in a overwrite set
        // False positives would be problematic as the code _must_ be persisted
        private readonly ClockKeyCacheNonConcurrent<ValueHash256> _persistedCodeInsertFilter = new(1_024);
        private readonly ClockKeyCacheNonConcurrent<ValueHash256> _blockCodeInsertFilter = new(256);
        private readonly Dictionary<AddressAsKey, int> _blockSlotIndex = new(4_096);
        private AddressAsKey[] _blockAddresses = new AddressAsKey[4_096];
        private Account?[] _blockBefore = new Account?[4_096];
        private Account?[] _blockAfter = new Account?[4_096];
        private int _blockSlotCount;

        private readonly ILogger _logger;
        private Dictionary<Hash256AsKey, byte[]> _codeBatch;
        private Dictionary<Hash256AsKey, byte[]>.AlternateLookup<ValueHash256> _codeBatchAlternate;
        internal IWorldStateScopeProvider.IScope? _tree;

        private bool _needsStateRootUpdate;
        private IWorldStateScopeProvider.ICodeDb? _codeDb;

        public StateProvider(
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
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
        }

        public int ChangedAccountCount => _blockSlotCount;

        public void SetScope(IWorldStateScopeProvider.IScope? scope)
        {
            _tree = scope;
            _codeDb = scope?.CodeDb;
        }

        public bool IsContract(Address address)
        {
            int slotIndex = GetSlotIndexFast(address);
            return _exists[slotIndex] && _hasCode[slotIndex];
        }

        public bool AccountExists(Address address)
        {
            int slotIndex = GetSlotIndexFast(address);
            return _exists[slotIndex];
        }

        public Account GetAccount(Address address) => GetAccountFromCache(address) ?? Account.TotallyEmpty;

        public bool IsDeadAccount(Address address)
        {
            int slotIndex = GetSlotIndexFast(address);
            return !_exists[slotIndex] || IsEmpty(slotIndex);
        }

        public UInt256 GetNonce(Address address)
        {
            int slotIndex = GetSlotIndexFast(address);
            return _exists[slotIndex] ? _nonces[slotIndex] : UInt256.Zero;
        }

        public ref readonly UInt256 GetBalance(Address address)
        {
            int slotIndex = GetSlotIndexFast(address);
            return ref _exists[slotIndex] ? ref _balances[slotIndex] : ref _zero;
        }

        public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            bool inserted = false;

            // Don't reinsert if already inserted. This can be the case when the same
            // code is used by multiple deployments. Either from factory contracts (e.g. LPs)
            // or people copy and pasting popular contracts
            if (!_blockCodeInsertFilter.Get(codeHash) && !_persistedCodeInsertFilter.Get(codeHash))
            {
                if (_codeBatch is null)
                {
                    _codeBatch = new(Hash256AsKeyComparer.Instance);
                    _codeBatchAlternate = _codeBatch.GetAlternateLookup<ValueHash256>();
                }
                _codeBatchAlternate[codeHash] = code.AsArray();

                _blockCodeInsertFilter.Set(codeHash);
                inserted = true;
            }

            int slotIndex = GetSlotIndexFast(address);
            if (!_exists[slotIndex])
            {
                ThrowIfNull(address);
            }

            Hash256? previousCodeHash = _codeHashes[slotIndex];
            if (previousCodeHash is null || previousCodeHash.ValueHash256 != codeHash)
            {
                _needsStateRootUpdate = true;
                if (_logger.IsDebug) Debug(address, codeHash, previousCodeHash);

                MutateSlotMeta(slotIndex, SlotFlags.None, MutationClearMask);
                Hash256? normalizedCodeHash = codeHash == Keccak.OfAnEmptyString.ValueHash256
                    ? null
                    : codeHash.ToHash256();
                _codeHashes[slotIndex] = normalizedCodeHash;
                _hasCode[slotIndex] = normalizedCodeHash is not null;
            }
            else if (spec.IsEip158Enabled && !isGenesis)
            {
                if (_logger.IsTrace) Trace(address);
                if (IsEmpty(slotIndex))
                {
                    ApplyTouch(slotIndex, spec, isZero: true);
                }
            }

            return inserted;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Debug(Address address, in ValueHash256 codeHash, Hash256? previousCodeHash)
                => _logger.Debug($"Update {address} C {previousCodeHash ?? Keccak.OfAnEmptyString} -> {codeHash}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address) => _logger.Trace($"Touch {address} (code hash)");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowIfNull(Address address)
                => throw new InvalidOperationException($"Account {address} is null when updating code hash");
        }

        private void SetNewBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSubtracting, bool incrementNonce = false)
        {
            int GetSlotIndexCheckExists()
            {
                int slotIndex = GetSlotIndexFast(address);
                if (!_exists[slotIndex])
                {
                    ThrowNonExistingAccount();
                }

                return slotIndex;

                [DoesNotReturn, StackTraceHidden]
                static void ThrowNonExistingAccount()
                    => throw new InvalidOperationException("Updating balance of a non-existing account");
            }

            bool isZero = balanceChange.IsZero;
            if (!incrementNonce && isZero)
            {
                // this also works like this in Geth (they don't follow the spec ¯\_(*~*)_/¯)
                // however we don't do it because of a consensus issue with Geth, just to avoid
                // hitting non-existing account when subtracting Zero-value from the sender
                if (releaseSpec.IsEip158Enabled && !isSubtracting)
                {
                    int touchedSlotIndex = GetSlotIndexCheckExists();

                    if (_logger.IsTrace) TraceTouch(address);
                    if (IsEmpty(touchedSlotIndex))
                    {
                        _needsStateRootUpdate = true;
                        ApplyTouch(touchedSlotIndex, releaseSpec, true);
                    }
                }

                return;
            }

            _needsStateRootUpdate = true;
            int slotIndex = GetSlotIndexCheckExists();
            UInt256 currentBalance = _balances[slotIndex];

            if (isSubtracting && currentBalance < balanceChange)
            {
                ThrowInsufficientBalanceException(address);
            }

            UInt256 newBalance;
            if (isZero)
            {
                newBalance = currentBalance;
            }
            else if (isSubtracting)
            {
                currentBalance.Subtract(in balanceChange, out newBalance);
            }
            else
            {
                currentBalance.Add(in balanceChange, out newBalance);
            }

            if (_logger.IsTrace) TraceUpdate(address, in balanceChange, isSubtracting, in currentBalance, in newBalance);

            MutateSlotMeta(slotIndex, SlotFlags.None, MutationClearMask);
            _balances[slotIndex] = newBalance;
            if (incrementNonce)
            {
                _nonces[slotIndex] += UInt256.One;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceTouch(Address address) => _logger.Trace($"Touch {address} (balance)");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceUpdate(Address address, in UInt256 balanceChange, bool isSubtracting, in UInt256 oldBalance, in UInt256 newBalance)
                => _logger.Trace($"Update {address} B {oldBalance.ToHexString(skipLeadingZeros: true)} -> {newBalance.ToHexString(skipLeadingZeros: true)} ({(isSubtracting ? "-" : "+")}{balanceChange})");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowInsufficientBalanceException(Address address)
                => throw new InsufficientBalanceException(address);
        }

        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec)
        {
            SetNewBalance(address, balanceChange, releaseSpec, isSubtracting: true);
        }

        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool incrementNonce = false)
        {
            SetNewBalance(address, balanceChange, releaseSpec, isSubtracting: false, incrementNonce);
        }

        public void IncrementNonce(Address address, UInt256 delta) => ChangeNonce(address, delta, subtract: false);

        public void DecrementNonce(Address address, UInt256 delta) => ChangeNonce(address, delta, subtract: true);

        private void ChangeNonce(Address address, UInt256 delta, bool subtract)
        {
            _needsStateRootUpdate = true;
            int slotIndex = GetSlotIndexFast(address);
            if (!_exists[slotIndex])
            {
                ThrowNullNonceAccount(address);
            }

            UInt256 oldNonce = _nonces[slotIndex];
            UInt256 newNonce = subtract ? oldNonce - delta : oldNonce + delta;
            if (_logger.IsTrace) Trace(address, in oldNonce, in newNonce);

            MutateSlotMeta(slotIndex, SlotFlags.None, MutationClearMask);
            _nonces[slotIndex] = newNonce;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, in UInt256 previousNonce, in UInt256 changedNonce)
                => _logger.Trace($"Update {address} N {previousNonce.ToHexString(skipLeadingZeros: true)} -> {changedNonce.ToHexString(skipLeadingZeros: true)}");
        }

        public ref readonly ValueHash256 GetCodeHash(Address address)
        {
            int slotIndex = GetSlotIndexFast(address);
            if (_exists[slotIndex] && _hasCode[slotIndex])
            {
                return ref _codeHashes[slotIndex]!.ValueHash256;
            }

            return ref Keccak.OfAnEmptyString.ValueHash256;
        }

        public byte[] GetCode(in ValueHash256 codeHash)
            => GetCodeCore(in codeHash);

        private byte[] GetCodeCore(in ValueHash256 codeHash)
        {
            if (codeHash == Keccak.OfAnEmptyString.ValueHash256) return [];

            if (_codeBatch is null || !_codeBatchAlternate.TryGetValue(codeHash, out byte[]? code))
            {
                code = _codeDb.GetCode(codeHash);
            }
            return code ?? ThrowMissingCode(in codeHash);

            [DoesNotReturn, StackTraceHidden]
            static byte[] ThrowMissingCode(in ValueHash256 codeHash)
                => throw new InvalidOperationException($"Code {codeHash} is missing from the database.");
        }

        public byte[] GetCode(Address address)
        {
            int slotIndex = GetSlotIndexFast(address);
            if (!_exists[slotIndex] || !_hasCode[slotIndex])
            {
                return [];
            }

            return GetCode(in _codeHashes[slotIndex]!.ValueHash256);
        }

        public void DeleteAccount(Address address)
        {
            _needsStateRootUpdate = true;
            int slotIndex = GetOrCreateSlotIndex(address);
            ApplyDelete(slotIndex);
        }

        public int TakeSnapshot()
        {
            int snapshot = _currentFrameId;
            int nextFrameId = snapshot + 1;
            EnsureFrameCapacity(_frameCount + 1);
            _frameUndoOffsets[_frameCount] = _undoCount;
            _frameIds[_frameCount] = nextFrameId;
            _frameCount++;
            _currentFrameId = nextFrameId;
            if (_logger.IsTrace) Trace(snapshot);
            return snapshot;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(int currentPosition) => _logger.Trace($"State snapshot {currentPosition}");
        }

        /// <summary>
        /// Restores the <see cref="StateProvider"/> to a prior frame snapshot.
        /// Rolls back all undo entries recorded after the specified <paramref name="snapshot"/> frame id.
        /// </summary>
        /// <param name="snapshot">Frame id returned by <see cref="TakeSnapshot"/>, or -1 to revert everything.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown if <paramref name="snapshot"/> is beyond the current frame position.</exception>
        public void Restore(int snapshot)
        {
            if (snapshot > _currentFrameId) ThrowCannotRestore(_currentFrameId, snapshot);
            if (_logger.IsTrace) Trace(snapshot);

            int startOffset;
            int keptFrameCount;
            if (snapshot < 0)
            {
                // Revert everything including the base frame
                startOffset = 0;
                keptFrameCount = 0;
                snapshot = 0;
            }
            else
            {
                keptFrameCount = _frameCount;
                while (keptFrameCount > 0 && _frameIds[keptFrameCount - 1] > snapshot)
                {
                    keptFrameCount--;
                }

                if (keptFrameCount == _frameCount)
                {
                    // No frames to pop. But there may be undo entries at the current
                    // frame level (mutations before any TakeSnapshot that need reverting
                    // when Restore(Snapshot.Empty) is called repeatedly).
                    if (_undoCount > 0 && keptFrameCount > 0
                        && _undoCount > _frameUndoOffsets[keptFrameCount - 1])
                    {
                        startOffset = _frameUndoOffsets[keptFrameCount - 1];
                        keptFrameCount--;
                    }
                    else
                    {
                        return;
                    }
                }
                else if (keptFrameCount == 0)
                {
                    startOffset = 0;
                }
                else
                {
                    startOffset = _frameUndoOffsets[keptFrameCount];
                }
            }

            int undoEnd = _undoCount;
            for (int frameIndex = _frameCount - 1; frameIndex >= keptFrameCount; frameIndex--)
            {
                int frameStart = _frameUndoOffsets[frameIndex];
                // Forward iteration per frame keeps access sequential while preserving correctness
                // when multiple nested frames touched the same slot.
                for (int i = frameStart; i < undoEnd; i++)
                {
                    ref readonly UndoEntry undoEntry = ref _undoBuffer[i];
                    int slotIndex = undoEntry.SlotIndex;
                    _exists[slotIndex] = undoEntry.PreviousExists;
                    _balances[slotIndex] = undoEntry.PreviousBalance;
                    _nonces[slotIndex] = undoEntry.PreviousNonce;
                    _codeHashes[slotIndex] = undoEntry.PreviousCodeHash;
                    _hasCode[slotIndex] = undoEntry.PreviousCodeHash is not null;
                    ref SlotMeta meta = ref _slotsMeta[slotIndex];
                    meta.Flags = undoEntry.PreviousFlags;
                    meta.OwnerFrameId = undoEntry.PreviousOwnerFrameId;
                    meta.StorageRoot = undoEntry.PreviousStorageRoot;
                }

                undoEnd = frameStart;
            }

            _undoCount = startOffset;
            _frameCount = keptFrameCount > 0 ? keptFrameCount : 1;

            // Always push a fresh frame after restore (even when no undo entries were
            // replayed). Trimming frames changes the frame topology; without a new boundary,
            // subsequent mutations create undo entries scoped to the wrong frame, causing a
            // later deeper Restore to replay too many entries.
            int newFrameId = _currentFrameId + 1;
            EnsureFrameCapacity(_frameCount + 1);
            _frameUndoOffsets[_frameCount] = startOffset;
            _frameIds[_frameCount] = newFrameId;
            _frameCount++;
            _currentFrameId = newFrameId;
            ClearInlineCache();

            // Local helpers to keep cold code from throws and string interpolation out of hot code.
            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(int snap) => _logger.Trace($"Restoring state snapshot {snap}");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowCannotRestore(int current, int snap)
                => throw new InvalidOperationException($"{nameof(StateProvider)} tried to restore snapshot {snap} beyond current position {current}");
        }

        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            _needsStateRootUpdate = true;
            if (_logger.IsTrace) Trace(address, balance, nonce);

            int slotIndex = GetOrCreateSlotIndex(address);
            ApplyNew(slotIndex, in balance, in nonce, null, null);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, in UInt256 balance, in UInt256 nonce)
                => _logger.Trace($"Creating account: {address} with balance {balance.ToHexString(skipLeadingZeros: true)} and nonce {nonce.ToHexString(skipLeadingZeros: true)}");
        }

        public void CreateEmptyAccountIfDeletedOrNew(Address address)
        {
            if (TryGetExistingSlotIndex(address, out int slotIndex))
            {
                ref SlotMeta meta = ref _slotsMeta[slotIndex];
                bool createdOrTouchedEmpty = _exists[slotIndex]
                    && IsEmpty(slotIndex)
                    && (meta.Flags & (SlotFlags.LatestNew | SlotFlags.Touched)) != 0;
                if ((meta.Flags & SlotFlags.Deleted) != 0 || createdOrTouchedEmpty)
                {
                    _needsStateRootUpdate = true;
                    if (_logger.IsTrace) Trace(address);
                    ApplyRecreateEmpty(slotIndex);
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

        public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balance, IReleaseSpec spec, bool incrementNonce = false)
        {
            if (AccountExists(address))
            {
                AddToBalance(address, balance, spec, incrementNonce);
                return false;
            }
            else
            {
                CreateAccount(address, balance, in incrementNonce ? ref UInt256.One : ref UInt256.Zero);
                return true;
            }
        }

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer stateTracer, bool commitRoots, bool isGenesis, Address? retainInCache = null)
        {
            Task codeFlushTask = !commitRoots || _codeBatch is null || _codeBatch.Count == 0
                ? Task.CompletedTask
                : CommitCodeAsync(_codeDb);

            bool isTracing = _logger.IsTrace;
            Dictionary<AddressAsKey, ChangeTrace>? trace = !stateTracer.IsTracingState ? null : [];
            HashSet<AddressAsKey>? nullAccountReads = trace is null ? null : [];

            if (_dirtyCount == 0)
            {
                if (isTracing) TraceNoChanges();

                if (trace is not null)
                {
                    CollectReadOnlyTrace(trace, nullAccountReads!);
                    trace.ReportStateTrace(stateTracer, nullAccountReads!, this);
                }

                Account? retainedReadOnlyAccount = retainInCache is null ? null : ResolveRetainedAccount(retainInCache);
                ResetTransactionSlots(retainInCache, retainedReadOnlyAccount);

                codeFlushTask.GetAwaiter().GetResult();
                return;
            }

            if (isTracing) TraceCommit(_dirtyCount);
            Span<int> dirtySlots = _dirtySlotIndices.AsSpan(0, _dirtyCount);
            for (int i = 0; i < dirtySlots.Length; i++)
            {
                int slotIndex = dirtySlots[i];
                ref SlotMeta meta = ref _slotsMeta[slotIndex];

                // Slot may have been reverted after being added to the dirty list
                if ((meta.Flags & SlotFlags.Dirty) == 0)
                    continue;

                Account? account = _exists[slotIndex] ? ReconstructAccount(slotIndex) : null;
                Address address = meta.Address;
                int blockSlotIndex = meta.BlockSlotIndex;
                Account? before = trace is null ? null : _blockAfter[blockSlotIndex];

                bool shouldApplyState;
                bool shouldReportTrace;
                Account? after;

                if ((meta.Flags & SlotFlags.RecreatedEmpty) != 0)
                {
                    shouldApplyState = true;
                    shouldReportTrace = true;
                    after = account;
                    if (isTracing && after is not null) TraceCreate(address, after);
                }
                else if ((meta.Flags & SlotFlags.Deleted) != 0)
                {
                    bool wasCreatedThisTransaction = (meta.Flags & SlotFlags.Created) != 0;
                    shouldApplyState = !wasCreatedThisTransaction;
                    shouldReportTrace = !wasCreatedThisTransaction;
                    after = null;
                    if (isTracing) TraceRemove(address);
                }
                else if (_exists[slotIndex]
                    && releaseSpec.IsEip158Enabled
                    && IsEmpty(slotIndex)
                    && !isGenesis)
                {
                    Account nonNullAccount = account!;
                    bool wasLatestChangeNew = (meta.Flags & SlotFlags.LatestNew) != 0;
                    shouldApplyState = !wasLatestChangeNew;
                    shouldReportTrace = !wasLatestChangeNew;
                    after = null;
                    if (isTracing && shouldApplyState) TraceRemoveEmpty(address, nonNullAccount);
                }
                else
                {
                    shouldApplyState = true;
                    shouldReportTrace = true;
                    after = account;
                    if (isTracing && after is not null)
                    {
                        if ((meta.Flags & SlotFlags.LatestNew) != 0) TraceCreate(address, after);
                        else TraceUpdate(address, after);
                    }
                }

                if (shouldApplyState)
                {
                    _blockAfter[blockSlotIndex] = after;
                    _needsStateRootUpdate = true;
                }

                if (trace is not null)
                {
                    if (shouldReportTrace)
                    {
                        trace[address] = new ChangeTrace(before, after);
                    }
                    else if ((meta.Flags & SlotFlags.NullRead) != 0)
                    {
                        // Address was read as non-existent before being mutated.
                        // Even though the mutation was suppressed (e.g. EIP-158 empty
                        // account deletion), the tracer needs to know it was accessed.
                        nullAccountReads!.Add(address);
                    }
                }
            }

            if (trace is not null)
            {
                CollectReadOnlyTrace(trace, nullAccountReads!);
                trace.ReportStateTrace(stateTracer, nullAccountReads!, this);
            }

            Account? retainedAccount = retainInCache is null ? null : ResolveRetainedAccount(retainInCache);
            ResetTransactionSlots(retainInCache, retainedAccount);

            codeFlushTask.GetAwaiter().GetResult();

            Task CommitCodeAsync(IWorldStateScopeProvider.ICodeDb codeDb)
            {
                Dictionary<Hash256AsKey, byte[]> dict = Interlocked.Exchange(ref _codeBatch, null);
                if (dict is null) return Task.CompletedTask;
                _codeBatchAlternate = default;

                return Task.Run(() =>
                {
                    using (var batch = codeDb.BeginCodeWrite())
                    {
                        // Insert ordered for improved RocksDB performance
                        Hash256AsKey[] keys = new Hash256AsKey[dict.Count];
                        dict.Keys.CopyTo(keys, 0);
                        Array.Sort(keys);
                        foreach (Hash256AsKey key in keys)
                        {
                            batch.Set(key.Value, dict[key]);
                            _persistedCodeInsertFilter.Set(key.Value.ValueHash256);
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
            void TraceCommit(int dirtyCount) => _logger.Trace($"Committing state changes (dirty slots: {dirtyCount})");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceNoChanges() => _logger.Trace("No state changes to commit");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceRemove(Address address) => _logger.Trace($"Commit remove {address}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCreate(Address address, Account account)
                => _logger.Trace($"Commit create {address} B = {account.Balance.ToHexString(skipLeadingZeros: true)} N = {account.Nonce.ToHexString(skipLeadingZeros: true)}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceUpdate(Address address, Account account)
                => _logger.Trace($"Commit update {address} B = {account.Balance.ToHexString(skipLeadingZeros: true)} N = {account.Nonce.ToHexString(skipLeadingZeros: true)} C = {account.CodeHash}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceRemoveEmpty(Address address, Account account)
                => _logger.Trace($"Commit remove empty {address} B = {account.Balance.ToHexString(skipLeadingZeros: true)} N = {account.Nonce.ToHexString(skipLeadingZeros: true)}");
        }

        internal void FlushToTree(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
        {
            int writes = 0;
            int skipped = 0;

            for (int i = 0; i < _blockSlotCount; i++)
            {
                Account? before = _blockBefore[i];
                Account? after = _blockAfter[i];
                if (before != after)
                {
                    _blockBefore[i] = after;
                    writeBatch.Set(_blockAddresses[i], after);
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

        public bool WarmUp(Address address)
            => GetState(address) is not null;

        private Account? GetState(Address address)
        {
            int blockSlotIndex = GetOrCreateBlockSlotIndex(address, loadFromState: true);
            return _blockAfter[blockSlotIndex];
        }

        internal void SetState(Address address, Account? account)
        {
            int blockSlotIndex = GetOrCreateBlockSlotIndex(address, loadFromState: false);
            _blockAfter[blockSlotIndex] = account;
            _needsStateRootUpdate = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Account? GetAccountFromCache(Address address)
        {
            int idx = GetSlotIndexFast(address);
            return ReconstructAccount(idx);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Account? ReconstructAccount(int slotIndex)
        {
            if (!_exists[slotIndex])
            {
                return null;
            }

            UInt256 balance = _balances[slotIndex];
            UInt256 nonce = _nonces[slotIndex];
            Hash256? codeHash = _codeHashes[slotIndex];
            Hash256? storageRoot = _slotsMeta[slotIndex].StorageRoot;
            if (codeHash is null && storageRoot is null && balance.IsZero && nonce.IsZero)
            {
                return Account.TotallyEmpty;
            }

            return new Account(nonce, balance, storageRoot ?? Keccak.EmptyTreeHash, codeHash ?? Keccak.OfAnEmptyString);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsEmpty(int slotIndex)
            => !_hasCode[slotIndex] && _balances[slotIndex].IsZero && _nonces[slotIndex].IsZero;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Hash256? NormalizeCodeHash(Hash256 codeHash)
            => codeHash == Keccak.OfAnEmptyString ? null : codeHash;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Hash256? NormalizeStorageRoot(Hash256 storageRoot)
            => storageRoot == Keccak.EmptyTreeHash ? null : storageRoot;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetSlotValues(int slotIndex, bool exists, in UInt256 balance, in UInt256 nonce, Hash256? codeHash, Hash256? storageRoot)
        {
            _exists[slotIndex] = exists;
            _balances[slotIndex] = balance;
            _nonces[slotIndex] = nonce;
            _codeHashes[slotIndex] = codeHash;
            _hasCode[slotIndex] = codeHash is not null;
            _slotsMeta[slotIndex].StorageRoot = storageRoot;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetSlotIndexFast(Address address)
        {
            int bucket = GetInlineCacheBucket(address);
            ref (Address? Key, int SlotIndex) entry = ref GetInlineCacheEntry(bucket);
            if (ReferenceEquals(entry.Key, address))
            {
                return entry.SlotIndex;
            }

            return GetSlotIndexSlow(address, bucket, loadFromState: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetOrCreateSlotIndex(Address address)
        {
            int bucket = GetInlineCacheBucket(address);
            ref (Address? Key, int SlotIndex) entry = ref GetInlineCacheEntry(bucket);
            if (ReferenceEquals(entry.Key, address))
            {
                return entry.SlotIndex;
            }

            return GetSlotIndexSlow(address, bucket, loadFromState: false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetExistingSlotIndex(Address address, out int slotIndex)
        {
            int bucket = GetInlineCacheBucket(address);
            ref (Address? Key, int SlotIndex) entry = ref GetInlineCacheEntry(bucket);
            if (ReferenceEquals(entry.Key, address))
            {
                slotIndex = entry.SlotIndex;
                return true;
            }

            if (_slotIndex.TryGetValue(address, out slotIndex))
            {
                entry.Key = address;
                entry.SlotIndex = slotIndex;
                return true;
            }

            slotIndex = -1;
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int GetSlotIndexSlow(Address address, int bucket, bool loadFromState)
        {
            ref int slotIndexRef = ref CollectionsMarshal.GetValueRefOrAddDefault(_slotIndex, address, out bool exists);
            if (!exists)
            {
                int slotIndex = _slotCount;
                EnsureSlotCapacity(slotIndex + 1);

                int blockSlotIndex = GetOrCreateBlockSlotIndex(address, loadFromState);
                Account? account = loadFromState ? _blockAfter[blockSlotIndex] : null;
                SlotFlags flags = loadFromState && account is null ? SlotFlags.NullRead : SlotFlags.None;
                if (account is null)
                {
                    _slotsMeta[slotIndex] = new SlotMeta
                    {
                        OwnerFrameId = -1,
                        DirtyGeneration = 0,
                        Flags = flags,
                        Address = address,
                        BlockSlotIndex = blockSlotIndex,
                        StorageRoot = null,
                    };
                    SetSlotValues(slotIndex, exists: false, in _zero, in _zero, null, null);
                }
                else
                {
                    Hash256? codeHash = NormalizeCodeHash(account.CodeHash);
                    Hash256? storageRoot = NormalizeStorageRoot(account.StorageRoot);
                    _slotsMeta[slotIndex] = new SlotMeta
                    {
                        OwnerFrameId = -1,
                        DirtyGeneration = 0,
                        Flags = flags,
                        Address = address,
                        BlockSlotIndex = blockSlotIndex,
                        StorageRoot = storageRoot,
                    };
                    SetSlotValues(slotIndex, exists: true, account.Balance, account.Nonce, codeHash, storageRoot);
                }

                _slotCount = slotIndex + 1;
                slotIndexRef = slotIndex;
            }

            ref (Address? Key, int SlotIndex) entry = ref GetInlineCacheEntry(bucket);
            entry.Key = address;
            entry.SlotIndex = slotIndexRef;
            return slotIndexRef;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetOrCreateBlockSlotIndex(Address address, bool loadFromState)
        {
            ref int blockSlotIndexRef = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockSlotIndex, address, out bool exists);
            if (!exists)
            {
                int blockSlotIndex = _blockSlotCount;
                EnsureBlockSlotCapacity(blockSlotIndex + 1);

                Account? account = null;
                if (loadFromState)
                {
                    Metrics.IncrementStateTreeReads();
                    account = _tree.Get(address);
                }

                _blockAddresses[blockSlotIndex] = address;
                _blockBefore[blockSlotIndex] = account;
                _blockAfter[blockSlotIndex] = account;
                _blockSlotCount = blockSlotIndex + 1;
                blockSlotIndexRef = blockSlotIndex;
            }
            else if (loadFromState)
            {
                Metrics.IncrementStateTreeCacheHits();
            }

            return blockSlotIndexRef;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetInlineCacheBucket(Address address) => RuntimeHelpers.GetHashCode(address) & 3;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref (Address? Key, int SlotIndex) GetInlineCacheEntry(int bucket)
        {
            switch (bucket)
            {
                case 0:
                    return ref _inlineCache0;
                case 1:
                    return ref _inlineCache1;
                case 2:
                    return ref _inlineCache2;
                default:
                    return ref _inlineCache3;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyTouch(int slotIndex, IReleaseSpec releaseSpec, bool isZero)
        {
            Address address = _slotsMeta[slotIndex].Address;
            if (isZero && address == releaseSpec.Eip158IgnoredAccount)
            {
                return;
            }

            ref SlotMeta meta = ref _slotsMeta[slotIndex];
            if ((meta.Flags & SlotFlags.Touched) != 0
                && (meta.Flags & (SlotFlags.Deleted | SlotFlags.RecreatedEmpty)) == 0)
            {
                return;
            }

            MutateSlotMeta(slotIndex, SlotFlags.Touched, SlotFlags.Deleted | SlotFlags.RecreatedEmpty | SlotFlags.LatestNew);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyDelete(int slotIndex)
        {
            MutateSlotMeta(slotIndex, SlotFlags.Deleted, SlotFlags.Touched | SlotFlags.RecreatedEmpty | SlotFlags.LatestNew);
            SetSlotValues(slotIndex, exists: false, in _zero, in _zero, null, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyNew(int slotIndex, in UInt256 balance, in UInt256 nonce, Hash256? codeHash, Hash256? storageRoot)
        {
            MutateSlotMeta(slotIndex, SlotFlags.Created | SlotFlags.LatestNew, SlotFlags.Deleted | SlotFlags.Touched | SlotFlags.RecreatedEmpty);
            SetSlotValues(slotIndex, exists: true, in balance, in nonce, codeHash, storageRoot);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyRecreateEmpty(int slotIndex)
        {
            MutateSlotMeta(slotIndex, SlotFlags.RecreatedEmpty, SlotFlags.Deleted | SlotFlags.Touched | SlotFlags.LatestNew);
            SetSlotValues(slotIndex, exists: true, in _zero, in _zero, null, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MutateSlotMeta(int slotIndex, SlotFlags setFlags, SlotFlags clearFlags)
        {
            ref SlotMeta meta = ref _slotsMeta[slotIndex];
            if (meta.OwnerFrameId < _currentFrameId)
            {
                SaveUndoAndUpdateFrame(slotIndex, ref meta);
            }

            SlotFlags flags = (meta.Flags | SlotFlags.Dirty | setFlags) & ~clearFlags;
            meta.Flags = flags;

            if (meta.DirtyGeneration != _dirtyGeneration)
            {
                TrackDirty(slotIndex, ref meta);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SaveUndoAndUpdateFrame(int slotIndex, ref SlotMeta meta)
        {
            EnsureUndoCapacity(_undoCount + 1);
            _undoBuffer[_undoCount++] = new UndoEntry(
                slotIndex,
                meta.OwnerFrameId,
                meta.Flags,
                _exists[slotIndex],
                _balances[slotIndex],
                _nonces[slotIndex],
                _codeHashes[slotIndex],
                meta.StorageRoot);
            meta.OwnerFrameId = _currentFrameId;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TrackDirty(int slotIndex, ref SlotMeta meta)
        {
            _dirtySlotIndices[_dirtyCount++] = slotIndex;
            meta.DirtyGeneration = _dirtyGeneration;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AdvanceDirtyGeneration()
        {
            if (_dirtyGeneration == int.MaxValue)
            {
                for (int i = 0; i < _slotCount; i++)
                {
                    _slotsMeta[i].DirtyGeneration = 0;
                }
                _dirtyGeneration = 1;
            }
            else
            {
                _dirtyGeneration++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GrowCapacity(int current, int required)
            => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(required, current));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureSlotCapacity(int requiredSize)
        {
            if (_exists.Length >= requiredSize) return;

            int newLength = GrowCapacity(_exists.Length, requiredSize);
            Array.Resize(ref _exists, newLength);
            Array.Resize(ref _balances, newLength);
            Array.Resize(ref _nonces, newLength);
            Array.Resize(ref _codeHashes, newLength);
            Array.Resize(ref _hasCode, newLength);
            Array.Resize(ref _slotsMeta, newLength);
            Array.Resize(ref _dirtySlotIndices, newLength);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureBlockSlotCapacity(int requiredSize)
        {
            if (_blockAddresses.Length >= requiredSize) return;

            int newLength = GrowCapacity(_blockAddresses.Length, requiredSize);
            Array.Resize(ref _blockAddresses, newLength);
            Array.Resize(ref _blockBefore, newLength);
            Array.Resize(ref _blockAfter, newLength);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureUndoCapacity(int requiredSize)
        {
            if (_undoBuffer.Length >= requiredSize) return;

            int newLength = GrowCapacity(_undoBuffer.Length, requiredSize);
            Array.Resize(ref _undoBuffer, newLength);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureFrameCapacity(int requiredSize)
        {
            if (_frameUndoOffsets.Length >= requiredSize) return;

            int newLength = GrowCapacity(_frameUndoOffsets.Length, requiredSize);

            Array.Resize(ref _frameUndoOffsets, newLength);
            Array.Resize(ref _frameIds, newLength);
        }

        private void CollectReadOnlyTrace(Dictionary<AddressAsKey, ChangeTrace> trace, HashSet<AddressAsKey> nullAccountReads)
        {
            for (int i = 0; i < _slotCount; i++)
            {
                ref SlotMeta meta = ref _slotsMeta[i];
                if ((meta.Flags & SlotFlags.Dirty) != 0 || trace.ContainsKey(meta.Address))
                {
                    continue;
                }

                if ((meta.Flags & SlotFlags.NullRead) != 0 || !_exists[i])
                {
                    nullAccountReads.Add(meta.Address);
                    continue;
                }

                Account account = ReconstructAccount(i)!;
                trace[meta.Address] = new ChangeTrace(account, account);
            }
        }

        private Account? ResolveRetainedAccount(Address address)
        {
            if (TryGetExistingSlotIndex(address, out int slotIndex))
            {
                return ReconstructAccount(slotIndex);
            }

            return _blockSlotIndex.TryGetValue(address, out int blockSlotIndex)
                ? _blockAfter[blockSlotIndex]
                : null;
        }

        private void ResetTransactionSlots(Address? retainInCache, Account? retainedAccount)
        {
            int previousSlotCount = _slotCount;
            int previousUndoCount = _undoCount;
            if (previousSlotCount > 0)
            {
                Array.Clear(_exists, 0, previousSlotCount);
                Array.Clear(_balances, 0, previousSlotCount);
                Array.Clear(_nonces, 0, previousSlotCount);
                Array.Clear(_codeHashes, 0, previousSlotCount);
                Array.Clear(_hasCode, 0, previousSlotCount);
                Array.Clear(_slotsMeta, 0, previousSlotCount);
            }
            if (previousUndoCount > 0)
            {
                Array.Clear(_undoBuffer, 0, previousUndoCount);
            }

            _slotIndex.Clear();
            _slotCount = 0;
            _undoCount = 0;
            _frameCount = 1;
            _frameUndoOffsets[0] = 0;
            _frameIds[0] = 0;
            _currentFrameId = 0;
            _dirtyCount = 0;
            AdvanceDirtyGeneration();
            ClearInlineCache();

            if (retainInCache is not null && retainedAccount is not null)
            {
                int retainedBlockSlotIndex = GetOrCreateBlockSlotIndex(retainInCache, loadFromState: false);
                _blockAfter[retainedBlockSlotIndex] = retainedAccount;

                EnsureSlotCapacity(1);
                Hash256? codeHash = NormalizeCodeHash(retainedAccount.CodeHash);
                Hash256? storageRoot = NormalizeStorageRoot(retainedAccount.StorageRoot);
                _slotsMeta[0] = new SlotMeta
                {
                    OwnerFrameId = -1,
                    DirtyGeneration = 0,
                    Flags = SlotFlags.None,
                    Address = retainInCache,
                    BlockSlotIndex = retainedBlockSlotIndex,
                    StorageRoot = storageRoot,
                };
                SetSlotValues(0, exists: true, retainedAccount.Balance, retainedAccount.Nonce, codeHash, storageRoot);
                _slotIndex[retainInCache] = 0;
                _slotCount = 1;

                int bucket = GetInlineCacheBucket(retainInCache);
                ref (Address? Key, int SlotIndex) cacheEntry = ref GetInlineCacheEntry(bucket);
                cacheEntry.Key = retainInCache;
                cacheEntry.SlotIndex = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClearInlineCache()
        {
            _inlineCache0 = default;
            _inlineCache1 = default;
            _inlineCache2 = default;
            _inlineCache3 = default;
        }

        public ArrayPoolList<AddressAsKey>? ChangedAddresses()
        {
            int count = _blockSlotCount;
            if (count == 0)
            {
                return null;
            }

            return new ArrayPoolList<AddressAsKey>(_blockAddresses.AsSpan(0, count));
        }

        public void Reset(bool resetBlockChanges = true)
        {
            if (_logger.IsTrace) Trace();
            if (resetBlockChanges)
            {
                _blockCodeInsertFilter.Clear();
                if (_blockSlotCount > 0)
                {
                    Array.Clear(_blockAddresses, 0, _blockSlotCount);
                    Array.Clear(_blockBefore, 0, _blockSlotCount);
                    Array.Clear(_blockAfter, 0, _blockSlotCount);
                    _blockSlotCount = 0;
                }

                _blockSlotIndex.Clear();
                _codeBatch?.Clear();
            }
            ResetTransactionSlots(null, null);
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
        internal void SetNonce(Address address, in UInt256 nonce)
        {
            _needsStateRootUpdate = true;
            int slotIndex = GetSlotIndexFast(address);
            if (!_exists[slotIndex])
            {
                ThrowNullNonceAccount(address);
            }

            UInt256 previousNonce = _nonces[slotIndex];
            if (_logger.IsTrace) Trace(address, in previousNonce, in nonce);

            MutateSlotMeta(slotIndex, SlotFlags.None, MutationClearMask);
            _nonces[slotIndex] = nonce;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, in UInt256 oldNonce, in UInt256 changedNonce)
                => _logger.Trace($"Update {address} N {oldNonce} -> {changedNonce}");
        }

        private struct SlotMeta
        {
            public int OwnerFrameId;
            public int DirtyGeneration;
            public int BlockSlotIndex;
            public SlotFlags Flags;
            public Address Address;
            public Hash256? StorageRoot;
        }

        private const SlotFlags MutationClearMask = SlotFlags.Deleted | SlotFlags.Touched | SlotFlags.RecreatedEmpty | SlotFlags.LatestNew;

        [Flags]
        private enum SlotFlags
        {
            None = 0,
            Created = 1,
            Deleted = 2,
            Touched = 4,
            Dirty = 8,
            NullRead = 16,
            RecreatedEmpty = 32,
            LatestNew = 64,
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowNullNonceAccount(Address address)
            => throw new InvalidOperationException($"Account {address} is null when changing nonce");

        private readonly struct UndoEntry
        {
            private const int OwnerFrameIdMask = 0x00FFFFFF;
            private const int PackedNoOwner = OwnerFrameIdMask;

            private readonly ulong _packed;
            public readonly bool PreviousExists;
            public readonly UInt256 PreviousBalance;
            public readonly UInt256 PreviousNonce;
            public readonly Hash256? PreviousCodeHash;
            public readonly Hash256? PreviousStorageRoot;

            public UndoEntry(
                int slotIndex,
                int previousOwnerFrameId,
                SlotFlags previousFlags,
                bool previousExists,
                in UInt256 previousBalance,
                in UInt256 previousNonce,
                Hash256? previousCodeHash,
                Hash256? previousStorageRoot)
            {
                uint ownerFrameId = previousOwnerFrameId < 0 ? PackedNoOwner : (uint)previousOwnerFrameId;
                _packed = (ulong)(uint)slotIndex
                    | ((ulong)(ownerFrameId & OwnerFrameIdMask) << 32)
                    | ((ulong)(byte)previousFlags << 56);
                PreviousExists = previousExists;
                PreviousBalance = previousBalance;
                PreviousNonce = previousNonce;
                PreviousCodeHash = previousCodeHash;
                PreviousStorageRoot = previousStorageRoot;
            }

            public int SlotIndex => (int)(uint)_packed;

            public int PreviousOwnerFrameId
            {
                get
                {
                    int ownerFrameId = (int)((_packed >> 32) & OwnerFrameIdMask);
                    return ownerFrameId == PackedNoOwner ? -1 : ownerFrameId;
                }
            }

            public SlotFlags PreviousFlags => (SlotFlags)(byte)(_packed >> 56);
        }

        internal struct ChangeTrace(Account? before, Account? after)
        {
            public ChangeTrace(Account? after) : this(null, after)
            {
            }

            public Account? Before { get; set; } = before;
            public Account? After { get; set; } = after;
        }
    }

    internal static class Extensions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReportStateTrace(this Dictionary<AddressAsKey, ChangeTrace>? trace, IWorldStateTracer stateTracer, HashSet<AddressAsKey> nullAccountReads, StateProvider stateProvider)
        {
            foreach (Address nullRead in nullAccountReads)
            {
                stateTracer.ReportAccountRead(nullRead);
            }
            ReportChanges(trace, stateTracer, stateProvider);
        }

        private static void ReportChanges(Dictionary<AddressAsKey, ChangeTrace> trace, IStateTracer stateTracer, StateProvider stateProvider)
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
                            : stateProvider.GetCode(in beforeCodeHash.ValueHash256);
                    byte[]? afterCode = afterCodeHash is null
                        ? null
                        : afterCodeHash == Keccak.OfAnEmptyString
                            ? []
                            : stateProvider.GetCode(in afterCodeHash.ValueHash256);

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
