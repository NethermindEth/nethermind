// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
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
        private readonly PersistentStorageProvider _persistentStorageProvider;
        private readonly TransientStorageProvider _transientStorageProvider;
        private IWorldStateScopeProvider.IScope? _currentScope;
        private bool _isInScope;
        private readonly ILogger _logger;

        protected readonly StateProvider StateProvider;

        public Hash256 StateRoot
        {
            get
            {
                GuardInScope();
                return StateProvider.StateRoot;
            }
        }

        protected WorldState(IWorldStateScopeProvider scopeProvider, StateProvider stateProvider, ILogManager? logManager)
        {
            ScopeProvider = scopeProvider;
            StateProvider = stateProvider;
            _persistentStorageProvider = new PersistentStorageProvider(StateProvider, logManager);
            _transientStorageProvider = new TransientStorageProvider(logManager);
            _logger = logManager.GetClassLogger<WorldState>();
        }

        public WorldState(IWorldStateScopeProvider scopeProvider, ILogManager? logManager)
            : this(scopeProvider, new StateProvider(logManager), logManager)
        {
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
        private void ThrowOutOfScope()
        {
            throw new InvalidOperationException($"{nameof(IWorldState)} must only be used within scope");
        }

        public Account GetAccount(Address address)
        {
            DebugGuardInScope();
            return StateProvider.GetAccount(address);
        }

        bool IAccountStateProvider.TryGetAccount(Address address, out AccountStruct account)
        {
            // Note: This call is for compatibility with `IAccountStateProvider` and should not be called directly by VM. Because its slower.
            account = StateProvider.GetAccount(address)
                .WithChangedStorageRoot(_persistentStorageProvider.GetStorageRoot(address))
                .ToStruct();

            return !account.IsTotallyEmpty;
        }

        UInt256 IAccountStateProvider.GetNonce(Address address)
        {
            return StateProvider.GetAccount(address).Nonce;
        }

        UInt256 IAccountStateProvider.GetBalance(Address address)
        {
            return StateProvider.GetAccount(address).Balance;
        }

        bool IAccountStateProvider.IsStorageEmpty(Address address)
        {
            return _persistentStorageProvider.IsStorageEmpty(address);
        }

        bool IAccountStateProvider.HasCode(Address address)
        {
            return StateProvider.GetAccount(address).HasCode;
        }

        public bool IsContract(Address address)
        {
            DebugGuardInScope();
            return StateProvider.IsContract(address);
        }

        public byte[] GetOriginal(in StorageCell storageCell)
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
            if (Out.IsTargetBlock)
                Out.Log($"state[{storageCell.Address}]", storageCell.Index.ToValueHash().ToString(), newValue.PadLeft(32).ToHexString(withZeroX: true));
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
            if (Out.IsTargetBlock)
                Out.Log($"state-t[{storageCell.Address}]", storageCell.Index.ToValueHash().ToString(), newValue.PadLeft(32).ToHexString(withZeroX: true));
            DebugGuardInScope();
            _transientStorageProvider.Set(storageCell, newValue);
        }
        public void Reset(bool resetBlockChanges = true)
        {
            DebugGuardInScope();
            StateProvider.Reset(resetBlockChanges);
            _persistentStorageProvider.Reset(resetBlockChanges);
            _transientStorageProvider.Reset(resetBlockChanges);
        }
        public void WarmUp(AccessList? accessList)
        {
            if (accessList?.IsEmpty == false)
            {
                foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
                {
                    bool exists = StateProvider.WarmUp(address);
                    foreach (UInt256 storage in storages)
                    {
                        _persistentStorageProvider.WarmUp(new StorageCell(address, in storage), isEmpty: !exists);
                    }
                }
            }
        }

        public void WarmUp(Address address) => StateProvider.WarmUp(address);
        public void ClearStorage(Address address)
        {
            DebugGuardInScope();
            _persistentStorageProvider.ClearStorage(address);
            _transientStorageProvider.ClearStorage(address);
        }
        public void RecalculateStateRoot()
        {
            DebugGuardInScope();
            StateProvider.RecalculateStateRoot();
        }
        public void DeleteAccount(Address address)
        {
            if (Out.IsTargetBlock)
                Out.Log("account", address.ToString(), "deleted");
            DebugGuardInScope();
            StateProvider.DeleteAccount(address);
        }
        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            if (Out.IsTargetBlock)
                Out.Log("account", address.ToString(), $"created[b={balance}, n={nonce}]");
            DebugGuardInScope();
            StateProvider.CreateAccount(address, balance, nonce);
        }

        public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            if (Out.IsTargetBlock)
                Out.Log("code", address.ToString(), codeHash.ToString());
            DebugGuardInScope();
            return StateProvider.InsertCode(address, codeHash, code, spec, isGenesis);
        }
        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            if (Out.IsTargetBlock)
                Out.Log("account", address.ToString(), $"balance+={balanceChange}");
            DebugGuardInScope();
            StateProvider.AddToBalance(address, balanceChange, spec);
        }
        public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            if (Out.IsTargetBlock)
                Out.Log("account", address.ToString(), $"balance+={balanceChange}, create-if-not-exists");
            DebugGuardInScope();
            return StateProvider.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            if (Out.IsTargetBlock)
                Out.Log("account", address.ToString(), $"balance-={balanceChange}");
            DebugGuardInScope();
            StateProvider.SubtractFromBalance(address, balanceChange, spec);
        }
        public void IncrementNonce(Address address, UInt256 delta)
        {
            if (Out.IsTargetBlock)
                Out.Log("account", address.ToString(), $"nonce+={delta}");
            DebugGuardInScope();
            StateProvider.IncrementNonce(address, delta);
        }
        public void DecrementNonce(Address address, UInt256 delta)
        {
            if (Out.IsTargetBlock)
                Out.Log("account", address.ToString(), $"nonce-={delta}");
            DebugGuardInScope();
            StateProvider.DecrementNonce(address, delta);
        }

        public void CommitTree(long blockNumber)
        {
            DebugGuardInScope();
            StateProvider.UpdateStateRootIfNeeded();
            _currentScope.Commit(blockNumber);
            _persistentStorageProvider.ClearStorageMap();
        }

        public UInt256 GetNonce(Address address)
        {
            DebugGuardInScope();
            return StateProvider.GetNonce(address);
        }

        public IDisposable BeginScope(BlockHeader? baseBlock)
        {
            if (Interlocked.CompareExchange(ref _isInScope, true, false))
            {
                throw new InvalidOperationException("Cannot create nested worldstate scope.");
            }

            if (_logger.IsTrace) _logger.Trace($"Beginning WorldState scope with baseblock {baseBlock?.ToString(BlockHeader.Format.Short) ?? "null"} with stateroot {baseBlock?.StateRoot?.ToString() ?? "null"}.");

            _currentScope = ScopeProvider.BeginScope(baseBlock);
            StateProvider.SetScope(_currentScope);
            _persistentStorageProvider.SetBackendScope(_currentScope);

            return new Reactive.AnonymousDisposable(() =>
            {
                Reset();
                StateProvider.SetScope(null);
                _currentScope.Dispose();
                _currentScope = null;
                _isInScope = false;
                if (_logger.IsTrace) _logger.Trace($"WorldState scope for baseblock {baseBlock?.ToString(BlockHeader.Format.Short) ?? "null"} closed");
            });
        }

        public bool IsInScope => _currentScope is not null;
        public IWorldStateScopeProvider ScopeProvider { get; }

        public ref readonly UInt256 GetBalance(Address address)
        {
            DebugGuardInScope();
            return ref StateProvider.GetBalance(address);
        }

        public ValueHash256 GetStorageRoot(Address address)
        {
            DebugGuardInScope();
            if (address == null) throw new ArgumentNullException(nameof(address));
            return _persistentStorageProvider.GetStorageRoot(address);
        }

        public byte[] GetCode(Address address)
        {
            DebugGuardInScope();
            return StateProvider.GetCode(address);
        }

        public byte[] GetCode(in ValueHash256 codeHash)
        {
            DebugGuardInScope();
            return StateProvider.GetCode(in codeHash);
        }

        public ref readonly ValueHash256 GetCodeHash(Address address)
        {
            DebugGuardInScope();
            return ref StateProvider.GetCodeHash(address);
        }

        ValueHash256 IAccountStateProvider.GetCodeHash(Address address)
        {
            DebugGuardInScope();
            return StateProvider.GetCodeHash(address);
        }

        public bool AccountExists(Address address)
        {
            DebugGuardInScope();
            return StateProvider.AccountExists(address);
        }
        public bool IsDeadAccount(Address address)
        {
            DebugGuardInScope();
            return StateProvider.IsDeadAccount(address);
        }

        public bool HasStateForBlock(BlockHeader? header)
        {
            return ScopeProvider.HasRoot(header);
        }

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        {
            if (Out.IsTargetBlock)
                Out.Log("state commit");

            DebugGuardInScope();
            _transientStorageProvider.Commit(tracer);
            _persistentStorageProvider.Commit(tracer);
            StateProvider.Commit(releaseSpec, tracer, commitRoots, isGenesis);

            if (commitRoots)
            {
                using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = _currentScope.StartWriteBatch(StateProvider.ChangedAccountCount);
                writeBatch.OnAccountUpdated += (_, updatedAccount) => StateProvider.SetState(updatedAccount.Address, updatedAccount.Account);
                _persistentStorageProvider.FlushToTree(writeBatch);
                StateProvider.FlushToTree(writeBatch);
            }
        }

        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            if (Out.IsTargetBlock)
                Out.Log("state snapshot");

            DebugGuardInScope();
            int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
            int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
            Snapshot.Storage storageSnapshot = new Snapshot.Storage(persistentSnapshot, transientSnapshot);
            int stateSnapshot = StateProvider.TakeSnapshot();
            return new Snapshot(storageSnapshot, stateSnapshot);
        }

        public void Restore(Snapshot snapshot)
        {
            if (Out.IsTargetBlock)
                Out.Log("state restore");

            DebugGuardInScope();
            _persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);
            _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);
            StateProvider.Restore(snapshot.StateSnapshot);
        }

        internal void Restore(int state, int persistentStorage, int transientStorage)
        {
            DebugGuardInScope();
            Restore(new Snapshot(new Snapshot.Storage(persistentStorage, transientStorage), state));
        }

        public void SetNonce(Address address, in UInt256 nonce)
        {
            DebugGuardInScope();
            StateProvider.SetNonce(address, nonce);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            DebugGuardInScope();
            StateProvider.CreateAccountIfNotExists(address, balance, nonce);
        }

        ArrayPoolList<AddressAsKey>? IWorldState.GetAccountChanges()
        {
            DebugGuardInScope();
            return StateProvider.ChangedAddresses();
        }

        public void ResetTransient()
        {
            DebugGuardInScope();
            _transientStorageProvider.Reset();
        }
    }
}
