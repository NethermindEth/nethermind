// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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

[assembly: InternalsVisibleTo("Ethereum.Test.Base")]
[assembly: InternalsVisibleTo("Ethereum.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]
[assembly: InternalsVisibleTo("Nethermind.Benchmark")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization")]

namespace Nethermind.State
{
    public class WorldState : IWorldState
    {
        internal readonly StateProvider _stateProvider;
        internal readonly PersistentStorageProvider _persistentStorageProvider;
        private readonly TransientStorageProvider _transientStorageProvider;
        // Per-scope counter accumulator shared with the providers and scope; folded into the global
        // Metrics in Commit/EndScope to avoid per-increment cross-thread contention.
        private readonly LocalMetrics _localMetrics = new();
        private IWorldStateScopeProvider.IScope? _currentScope;
        private bool _isInScope;
        private readonly ILogger _logger;

        public Hash256 StateRoot
        {
            get
            {
                GuardInScope();
                return _stateProvider.StateRoot;
            }
        }

        public WorldState(
            IWorldStateScopeProvider scopeProvider,
            ILogManager? logManager)
        {
            ScopeProvider = scopeProvider;
            _stateProvider = new StateProvider(logManager, _localMetrics);
            _persistentStorageProvider = new PersistentStorageProvider(_stateProvider, logManager, _localMetrics);
            _transientStorageProvider = new TransientStorageProvider(logManager);
            _logger = logManager.GetClassLogger<WorldState>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GuardInScope()
        {
            if (_currentScope is null) ThrowOutOfScope();
        }

        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DebugGuardInScope()
        {
#if DEBUG
            if (_currentScope is null) ThrowOutOfScope();
#endif
        }

        [StackTraceHidden, DoesNotReturn]
        private void ThrowOutOfScope() => throw new InvalidOperationException($"{nameof(IWorldState)} must only be used within scope");

        public Account GetAccount(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetAccount(address);
        }

        public bool TryGetAccount(Address address, out AccountStruct account)
        {
            // Note: This call is for compatibility with `IAccountStateProvider` and should not be called directly by VM. Because its slower.
            account = _stateProvider.GetAccount(address)
                .WithChangedStorageRoot(_persistentStorageProvider.GetStorageRoot(address))
                .ToStruct();

            return !account.IsTotallyEmpty;
        }

        public bool IsContract(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.IsContract(address);
        }

        public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
        {
            DebugGuardInScope();
            return _persistentStorageProvider.GetOriginal(storageCell);
        }
        public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        {
            DebugGuardInScope();
            return _persistentStorageProvider.Get(storageCell);
        }
        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            DebugGuardInScope();
            _persistentStorageProvider.Set(storageCell, newValue);
        }
        public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        {
            DebugGuardInScope();
            return _transientStorageProvider.Get(storageCell);
        }
        public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        {
            DebugGuardInScope();
            _transientStorageProvider.Set(storageCell, newValue);
        }
        public void Reset(bool resetBlockChanges = true)
        {
            DebugGuardInScope();
            _stateProvider.Reset(resetBlockChanges);
            _persistentStorageProvider.Reset(resetBlockChanges);
            _transientStorageProvider.Reset(resetBlockChanges);
        }
        public void WarmUp(AccessList? accessList, CancellationToken cancellationToken = default)
        {
            if (accessList?.IsEmpty == false)
            {
                // Bail once cancelled (block done) so an over-declared list can't stall the end-of-block join.
                const int cancellationCheckMask = 0x3F; // check the token once per 64 warmed entries
                int warmed = 0;
                foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
                {
                    if ((++warmed & cancellationCheckMask) == 0 && cancellationToken.IsCancellationRequested) return;
                    bool exists = _stateProvider.WarmUp(address);
                    foreach (UInt256 storage in storages)
                    {
                        if ((++warmed & cancellationCheckMask) == 0 && cancellationToken.IsCancellationRequested) return;
                        _persistentStorageProvider.WarmUp(new StorageCell(address, in storage), isEmpty: !exists);
                    }
                }
            }
        }

        public void WarmUp(Address address) => _stateProvider.WarmUp(address);
        public void ClearStorage(Address address)
        {
            DebugGuardInScope();
            _persistentStorageProvider.ClearStorage(address);
            _transientStorageProvider.ClearStorage(address);
        }
        public void MarkStorageDestroyed(Address address)
        {
            DebugGuardInScope();
            _persistentStorageProvider.MarkStorageDestroyed(address);
            _transientStorageProvider.ClearStorage(address);
        }
        public void RecalculateStateRoot()
        {
            DebugGuardInScope();
            _stateProvider.RecalculateStateRoot();
        }
        public void DeleteAccount(Address address)
        {
            DebugGuardInScope();
            _stateProvider.DeleteAccount(address);
        }
        public void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
        {
            DebugGuardInScope();
            _stateProvider.CreateAccount(address, balance, nonce);
        }

        public void CreateEmptyAccountIfDeleted(Address address) => _stateProvider.CreateEmptyAccountIfDeletedOrNew(address);

        public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            DebugGuardInScope();
            return _stateProvider.InsertCode(address, codeHash, code, spec, isGenesis);
        }

        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        {
            DebugGuardInScope();
            _stateProvider.AddToBalance(address, balanceChange, spec, out oldBalance);
        }
        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
            => AddToBalance(address, balanceChange, spec, out UInt256 oldBalance);
        public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        {
            DebugGuardInScope();
            return _stateProvider.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out oldBalance);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        {
            DebugGuardInScope();
            _stateProvider.SubtractFromBalance(address, balanceChange, spec, out oldBalance);
        }
        public void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
        {
            DebugGuardInScope();
            _stateProvider.IncrementNonce(address, delta, out oldNonce);
        }
        public void DecrementNonce(Address address, ulong delta)
        {
            DebugGuardInScope();
            _stateProvider.DecrementNonce(address, delta);
        }

        public void CommitTree(ulong blockNumber)
        {
            DebugGuardInScope();
            _stateProvider.UpdateStateRootIfNeeded();
            _currentScope.Commit(blockNumber);
            _persistentStorageProvider.ClearStorageMap();
        }

        public ulong GetNonce(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetNonce(address);
        }

        public bool IsStorageEmpty(Address address) => _persistentStorageProvider.IsStorageEmpty(address);

        public bool HasCode(Address address) => _stateProvider.GetAccount(address).HasCode;

        public IDisposable BeginScope(BlockHeader? baseBlock)
        {
            if (Interlocked.CompareExchange(ref _isInScope, true, false))
            {
                throw new InvalidOperationException("Cannot create nested worldstate scope.");
            }

            if (_logger.IsTrace) _logger.Trace($"Beginning WorldState scope with baseblock {baseBlock?.ToString(BlockHeader.Format.Short) ?? "null"} with stateroot {baseBlock?.StateRoot?.ToString() ?? "null"}.");

            try
            {
                _currentScope = ScopeProvider.BeginScope(baseBlock, _localMetrics);
                _stateProvider.SetScope(_currentScope);
                _persistentStorageProvider.SetBackendScope(_currentScope);
            }
            catch
            {
                EndScope();
                throw;
            }

            if (_currentScope is IWorldStateScopeProvider.ISparseDeltaSink sink && sink.WantsCommittedDeltas)
            {
                _stateProvider.CommittedAccountSink = sink.OnCommittedAccount;
                if (sink.WantsCommittedStorageDeltas)
                {
                    _persistentStorageProvider.CommittedStorageSink =
                        (in StorageCell cell, byte[] value) => sink.OnCommittedStorage(in cell, value);
                }
            }

            return new Reactive.AnonymousDisposable(() =>
            {
                EndScope();
                if (_logger.IsTrace) _logger.Trace($"WorldState scope for baseblock {baseBlock?.ToString(BlockHeader.Format.Short) ?? "null"} closed");
            });
        }

        private void EndScope()
        {
            try
            {
                if (_currentScope is not null)
                {
                    // Fold any counters accumulated outside a Commit (e.g. prewarmer read warming) before the scope closes.
                    _localMetrics.Flush();
                    Reset();
                    _stateProvider.CommittedAccountSink = null;
                    _persistentStorageProvider.CommittedStorageSink = null;
                    _stateProvider.SetScope(null);
                    _currentScope.Dispose();
                }
            }
            finally
            {
                _currentScope = null;
                _isInScope = false;
            }
        }

        public bool IsInScope => _currentScope is not null;
        public IWorldStateScopeProvider ScopeProvider { get; }

        public Task HintBal(ReadOnlyBlockAccessList bal)
        {
            GuardInScope();
            return _currentScope!.HintBal(bal);
        }

        public ref readonly UInt256 GetBalance(Address address)
        {
            DebugGuardInScope();
            return ref _stateProvider.GetBalance(address);
        }

        public ValueHash256 GetStorageRoot(Address address)
        {
            DebugGuardInScope();
            ArgumentNullException.ThrowIfNull(address);
            return _persistentStorageProvider.GetStorageRoot(address);
        }

        public byte[] GetCode(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetCode(address);
        }

        public byte[] GetCode(in ValueHash256 codeHash)
        {
            DebugGuardInScope();
            return _stateProvider.GetCode(in codeHash);
        }

        public ref readonly ValueHash256 GetCodeHash(Address address)
        {
            DebugGuardInScope();
            return ref _stateProvider.GetCodeHash(address);
        }

        public bool AccountExists(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.AccountExists(address);
        }

        public bool IsNonZeroAccount(Address address, out bool accountExists)
        {
            DebugGuardInScope();
            Account? account = _stateProvider.GetThroughCache(address);
            accountExists = account is not null;
            return accountExists && (account!.IsContract || account.Nonce != 0 || !_persistentStorageProvider.IsStorageEmpty(address));
        }

        public bool IsDeadAccount(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.IsDeadAccount(address);
        }

        public bool HasStateForBlock(BlockHeader? header) => ScopeProvider.HasRoot(header);

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        {
            DebugGuardInScope();
            _transientStorageProvider.Commit(tracer);
            _persistentStorageProvider.Commit(tracer);
            _stateProvider.Commit(releaseSpec, tracer, commitRoots, isGenesis);

            if (_currentScope is IWorldStateScopeProvider.ISparseDeltaSink sparseDeltaSink
                && sparseDeltaSink.WantsCommittedDeltas)
            {
                sparseDeltaSink.OnCommitPhaseCompleted(commitRoots);
            }

            if (commitRoots)
            {
                using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = _currentScope.StartWriteBatch(_stateProvider.ChangedAccountCount);
                writeBatch.OnAccountUpdated += (_, updatedAccount) => _stateProvider.SetState(updatedAccount.Address, updatedAccount.Account);
                _persistentStorageProvider.FlushToTree(writeBatch);
                _stateProvider.FlushToTree(writeBatch);
            }

            // Fold this scope's accumulated counters into the global metrics. Runs per-tx commit and
            // at block-end on the block-processing thread, keeping ProcessingStats' MainThread* deltas current.
            _localMetrics.Flush();
        }

        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            DebugGuardInScope();
            int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
            int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
            Snapshot.Storage storageSnapshot = new(persistentSnapshot, transientSnapshot);
            int stateSnapshot = _stateProvider.TakeSnapshot();
            return new Snapshot(storageSnapshot, stateSnapshot, -1);
        }

        public void Restore(Snapshot snapshot)
        {
            DebugGuardInScope();
            _persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);
            _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);
            _stateProvider.Restore(snapshot.StateSnapshot);
        }

        internal void Restore(int state, int persistentStorage, int transientStorage)
        {
            DebugGuardInScope();
            Restore(new Snapshot(new Snapshot.Storage(persistentStorage, transientStorage), state, -1));
        }

        public void SetNonce(Address address, in ulong nonce)
        {
            DebugGuardInScope();
            _stateProvider.SetNonce(address, nonce);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default)
        {
            DebugGuardInScope();
            _stateProvider.CreateAccountIfNotExists(address, balance, nonce);
        }

        ArrayPoolList<AddressAsKey>? IWorldState.GetAccountChanges()
        {
            DebugGuardInScope();
            return _stateProvider.ChangedAddresses();
        }

        public void ResetTransient()
        {
            DebugGuardInScope();
            _transientStorageProvider.Reset();
        }
    }
}
