// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
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
            _stateProvider = new StateProvider(logManager);
            _persistentStorageProvider = new PersistentStorageProvider(_stateProvider, logManager);
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
        private void ThrowOutOfScope()
        {
            throw new InvalidOperationException($"{nameof(IWorldState)} must only be used within scope");
        }

        public Account GetAccount(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetAccount(address);
        }

        bool IAccountStateProvider.TryGetAccount(Address address, out AccountStruct account)
        {
            // Note: This call is for compatibility with `IAccountStateProvider` and should not be called directly by VM. Because its slower.
            account = _stateProvider.GetAccount(address)
                .WithChangedStorageRoot(_persistentStorageProvider.GetStorageRoot(address))
                .ToStruct();

            return !account.IsTotallyEmpty;
        }

        UInt256 IAccountStateProvider.GetNonce(Address address)
        {
            return _stateProvider.GetAccount(address).Nonce;
        }

        UInt256 IAccountStateProvider.GetBalance(Address address)
        {
            return _stateProvider.GetAccount(address).Balance;
        }

        bool IAccountStateProvider.IsStorageEmpty(Address address)
        {
            return _persistentStorageProvider.IsStorageEmpty(address);
        }

        bool IAccountStateProvider.HasCode(Address address)
        {
            return _stateProvider.GetAccount(address).HasCode;
        }

        public bool IsContract(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.IsContract(address);
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
        public void WarmUp(AccessList? accessList)
        {
            if (accessList?.IsEmpty == false)
            {
                foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
                {
                    bool exists = _stateProvider.WarmUp(address);
                    foreach (UInt256 storage in storages)
                    {
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
        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        {
            DebugGuardInScope();
            _stateProvider.CreateAccount(address, balance, nonce);
        }

        public void CreateEmptyAccountIfDeleted(Address address)
        {
            _stateProvider.CreateEmptyAccountIfDeletedOrNew(address);
        }

        public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            DebugGuardInScope();
            return _stateProvider.InsertCode(address, codeHash, code, spec, isGenesis);
        }
        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            DebugGuardInScope();
            _stateProvider.AddToBalance(address, balanceChange, spec);
        }
        public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            DebugGuardInScope();
            return _stateProvider.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            DebugGuardInScope();
            _stateProvider.SubtractFromBalance(address, balanceChange, spec);
        }
        public void IncrementNonce(Address address, UInt256 delta)
        {
            DebugGuardInScope();
            _stateProvider.IncrementNonce(address, delta);
        }
        public void DecrementNonce(Address address, UInt256 delta)
        {
            DebugGuardInScope();
            _stateProvider.DecrementNonce(address, delta);
        }

        public Account? GetAccountDirect(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetState(address);
        }

        public void ApplyPlainTransferDirect(
            Address sender, UInt256 newSenderNonce,
            in UInt256 senderGasReservation, in UInt256 senderRefund,
            Address recipient, in UInt256 transferValue,
            Address beneficiary, in UInt256 beneficiaryFee,
            Address? feeCollector, in UInt256 collectedFees,
            IReleaseSpec spec)
        {
            DebugGuardInScope();
            StateProvider sp = _stateProvider;

            // Sender: apply gas cost + value deduction + refund + nonce increment in one shot
            Account senderAccount = sp.GetState(sender) ?? Account.TotallyEmpty;
            UInt256 newBalance = senderAccount.Balance - senderGasReservation - transferValue + senderRefund;
            sp.SetState(sender, new Account(newSenderNonce, newBalance, senderAccount.StorageRoot, senderAccount.CodeHash));

            // Recipient: always process – the slow path (VM) always calls
            // AddToBalanceAndCreateIfNotExists(recipient, value) which touches the account.
            // EIP-158 deletes empty accounts that were touched.
            ApplyBalanceChange(sp, recipient, in transferValue, spec);

            // Beneficiary: always process – the slow path always calls
            // AddToBalanceAndCreateIfNotExists(beneficiary, fee).
            ApplyBalanceChange(sp, beneficiary, in beneficiaryFee, spec);

            // Fee collector (EIP-1559)
            if (feeCollector is not null && !collectedFees.IsZero)
            {
                Account? fcAccount = sp.GetState(feeCollector);
                if (fcAccount is not null)
                    sp.SetState(feeCollector, fcAccount.WithChangedBalance(fcAccount.Balance + collectedFees));
                else
                    sp.SetState(feeCollector, new Account(collectedFees));
            }
        }

        /// <summary>
        /// Applies a balance change matching the behavior of AddToBalanceAndCreateIfNotExists + EIP-158 cleanup.
        /// Respects IsEip158IgnoredAccount (e.g. AuRa's Address.SystemUser).
        /// </summary>
        private static void ApplyBalanceChange(StateProvider sp, Address address, in UInt256 amount, IReleaseSpec spec)
        {
            Account? account = sp.GetState(address);
            if (account is not null)
            {
                if (!amount.IsZero)
                {
                    sp.SetState(address, account.WithChangedBalance(account.Balance + amount));
                }
                else if (spec.IsEip158Enabled && account.IsEmpty && !spec.IsEip158IgnoredAccount(address))
                {
                    // Slow path touches the account with 0 balance → Commit deletes it via EIP-158
                    sp.SetState(address, null);
                }
                // else: non-empty account + 0 amount → no state change
            }
            else
            {
                if (!amount.IsZero)
                {
                    sp.SetState(address, new Account(amount));
                }
                else if (!spec.IsEip158Enabled)
                {
                    // Pre-EIP-158: slow path creates empty account
                    sp.SetState(address, new Account(UInt256.Zero));
                }
                // else: EIP-158 + 0 amount + non-existent → slow path creates then discards → no-op
            }
        }

        public void CommitTree(long blockNumber)
        {
            DebugGuardInScope();
            _stateProvider.UpdateStateRootIfNeeded();
            _currentScope.Commit(blockNumber);
            _persistentStorageProvider.ClearStorageMap();
        }

        public UInt256 GetNonce(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetNonce(address);
        }

        public IDisposable BeginScope(BlockHeader? baseBlock)
        {
            if (Interlocked.CompareExchange(ref _isInScope, true, false))
            {
                throw new InvalidOperationException("Cannot create nested worldstate scope.");
            }

            if (_logger.IsTrace) _logger.Trace($"Beginning WorldState scope with baseblock {baseBlock?.ToString(BlockHeader.Format.Short) ?? "null"} with stateroot {baseBlock?.StateRoot?.ToString() ?? "null"}.");

            _currentScope = ScopeProvider.BeginScope(baseBlock);
            _stateProvider.SetScope(_currentScope);
            _persistentStorageProvider.SetBackendScope(_currentScope);

            return new Reactive.AnonymousDisposable(() =>
            {
                Reset();
                _stateProvider.SetScope(null);
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
            return ref _stateProvider.GetBalance(address);
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

        ValueHash256 IAccountStateProvider.GetCodeHash(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.GetCodeHash(address);
        }

        public bool AccountExists(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.AccountExists(address);
        }
        public bool IsDeadAccount(Address address)
        {
            DebugGuardInScope();
            return _stateProvider.IsDeadAccount(address);
        }

        public bool HasStateForBlock(BlockHeader? header)
        {
            return ScopeProvider.HasRoot(header);
        }

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        {
            DebugGuardInScope();
            _transientStorageProvider.Commit(tracer);
            _persistentStorageProvider.Commit(tracer);
            _stateProvider.Commit(releaseSpec, tracer, commitRoots, isGenesis);

            if (commitRoots)
            {
                using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = _currentScope.StartWriteBatch(_stateProvider.ChangedAccountCount);
                writeBatch.OnAccountUpdated += (_, updatedAccount) => _stateProvider.SetState(updatedAccount.Address, updatedAccount.Account);
                _persistentStorageProvider.FlushToTree(writeBatch);
                _stateProvider.FlushToTree(writeBatch);
            }
        }

        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            DebugGuardInScope();
            int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
            int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
            Snapshot.Storage storageSnapshot = new Snapshot.Storage(persistentSnapshot, transientSnapshot);
            int stateSnapshot = _stateProvider.TakeSnapshot();
            return new Snapshot(storageSnapshot, stateSnapshot);
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
            Restore(new Snapshot(new Snapshot.Storage(persistentStorage, transientStorage), state));
        }

        public void SetNonce(Address address, in UInt256 nonce)
        {
            DebugGuardInScope();
            _stateProvider.SetNonce(address, nonce);
        }

        public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
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

        public void UpdatePreBlockCaches()
        {
            if (ScopeProvider is not IPreBlockCaches { Caches: { } preBlockCaches }) return;

            KeyValuePair<AddressAsKey, Account?>[] stateChanges = _stateProvider.CaptureCommittedStateChanges();
            if (stateChanges.Length > 0)
            {
                preBlockCaches.UpdateStateCache(stateChanges);
            }

            KeyValuePair<StorageCell, byte[]>[] storageChanges = _persistentStorageProvider.CaptureCommittedStorageChanges();
            if (storageChanges.Length > 0)
            {
                preBlockCaches.UpdateStorageCache(storageChanges);
            }
        }
    }
}
