// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.TxPool;

public class NonceManager : INonceManager, IDisposable
{
    private static readonly TimeSpan DisposeEvictionTimeout = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<AddressAsKey, AddressNonceManager> _addressNonceManagers = new();
    private readonly IChainHeadInfoProvider _chainHeadInfoProvider;
    private readonly IStateReader _stateReader;
    private readonly IStateHeaderProvider _stateHeaderProvider;
    private readonly ILogger _logger;
    private volatile Task _eviction = Task.CompletedTask;
    private int _sweepRunning;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a nonce manager that subscribes to <see cref="IChainHeadInfoProvider.HeadChanged"/> to drop
    /// per-address state once finalized state covers it; the instance must be disposed to unsubscribe.
    /// </summary>
    public NonceManager(IChainHeadInfoProvider chainHeadInfoProvider, IStateReader stateReader, IStateHeaderProvider stateHeaderProvider, ILogManager logManager)
    {
        _chainHeadInfoProvider = chainHeadInfoProvider;
        _stateReader = stateReader;
        _stateHeaderProvider = stateHeaderProvider;
        _logger = logManager.GetClassLogger<NonceManager>();
        _chainHeadInfoProvider.HeadChanged += OnHeadChanged;
    }

    internal Task Eviction => _eviction;

    public NonceLocker ReserveNonce(Address address, out ulong reservedNonce)
    {
        NonceLocker locker = LockAddressNonceManager(address, out AddressNonceManager addressNonceManager, out ulong accountNonce);
        reservedNonce = addressNonceManager.ReserveNonce(accountNonce);
        return locker;
    }

    public NonceLocker TxWithNonceReceived(Address address, ulong nonce)
    {
        NonceLocker locker = LockAddressNonceManager(address, out AddressNonceManager addressNonceManager, out ulong accountNonce);
        addressNonceManager.TxWithNonceReceived(accountNonce, nonce);
        return locker;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Waits a bounded time for an in-flight sweep, which stops after its current read, so it does not keep reading
    /// state that is being disposed, while a read stuck in a state backend does not hold up shutdown.
    /// </remarks>
    public void Dispose()
    {
        _disposed = true;
        _chainHeadInfoProvider.HeadChanged -= OnHeadChanged;
        if (Task.WaitAny([_eviction], DisposeEvictionTimeout) < 0 && _logger.IsDebug)
            _logger.Debug("Nonce eviction sweep did not finish before disposal");
    }

    internal bool TryGetUsedNonceCount(Address address, out int usedNonceCount)
    {
        bool isTracked = _addressNonceManagers.TryGetValue(address, out AddressNonceManager? addressNonceManager);
        usedNonceCount = isTracked ? addressNonceManager!.UsedNonceCount : 0;
        return isTracked;
    }

    /// <remarks>
    /// A manager can be evicted between <c>GetOrAdd</c> and acquiring its lock; retrying on the tombstone
    /// guarantees every holder works on the instance currently in the dictionary, so an address never has
    /// two live locks. The account nonce is read under the lock because one read before an eviction can be
    /// older than the nonces the evicted manager had already handed out.
    /// </remarks>
    private NonceLocker LockAddressNonceManager(Address address, out AddressNonceManager addressNonceManager, out ulong accountNonce)
    {
        while (true)
        {
            addressNonceManager = _addressNonceManagers.GetOrAdd(address, static _ => new AddressNonceManager());
            NonceLocker locker = addressNonceManager.Lock();
            if (addressNonceManager.IsEvicted)
            {
                locker.Dispose();
                continue;
            }

            try
            {
                accountNonce = _chainHeadInfoProvider.ReadOnlyStateProvider.GetNonce(address);
            }
            catch
            {
                locker.Dispose();
                throw;
            }

            return locker;
        }
    }

    /// <remarks>
    /// <see cref="IChainHeadInfoProvider.HeadChanged"/> has several raisers on different threads; the flag admits one sweep
    /// at a time, so <see cref="Dispose"/> waits on the only sweep that can be reading.
    /// </remarks>
    private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
    {
        if (_disposed || _addressNonceManagers.IsEmpty || Interlocked.CompareExchange(ref _sweepRunning, 1, 0) != 0) return;

        _eviction = Task.Run(() =>
        {
            try
            {
                EvictCaughtUpAddresses();
            }
            finally
            {
                Volatile.Write(ref _sweepRunning, 0);
            }
        });
    }

    /// <summary>
    /// Drops every manager that holds nothing a fresh one, seeded from finalized state, would not.
    /// </summary>
    /// <remarks>
    /// The nonce compared is the account's at a finality-safe block rather than at the head: a reorg can lower the
    /// head nonce, but never below the finalized one, so a manager recreated after an eviction never restarts below
    /// the counter the evicted one had reached. The block is clamped to <see cref="Reorganization.MaxDepth"/> below
    /// the head because finalized headers above that depth are not served even post-merge. When that header or its
    /// state is unavailable, only managers that never handed out a nonce are dropped: a fresh one equals them whatever
    /// the account's nonce, so a long finality stall does not let rejected transactions pile entries up.
    /// A failing read keeps only its own entry and is logged once per sweep.
    /// </remarks>
    internal void EvictCaughtUpAddresses()
    {
        try
        {
            ulong target = Math.Min(_stateHeaderProvider.FinalizedBlockNumber, _chainHeadInfoProvider.HeadNumber.SaturatingSub(Reorganization.MaxDepth));
            BlockHeader? finalizedHeader = _stateHeaderProvider.GetFinalizedHeader(target);
            if (finalizedHeader is not null && !_stateReader.HasStateForBlock(finalizedHeader)) finalizedHeader = null;

            int failedReads = 0;
            Exception? firstFailure = null;
            foreach (KeyValuePair<AddressAsKey, AddressNonceManager> entry in _addressNonceManagers)
            {
                if (_disposed) break;

                ulong? finalizedNonce = null;
                // A never-used manager is dropped whatever the nonce, so it needs no state read.
                if (finalizedHeader is not null && !entry.Value.IsUnused)
                {
                    try
                    {
                        finalizedNonce = _stateReader.TryGetAccount(finalizedHeader, entry.Key, out AccountStruct account) ? account.Nonce : 0;
                    }
                    catch (Exception e) when (e is not MissingTrieNodeException)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Could not read the finalized nonce of {entry.Key.Value}; keeping its nonce manager. {e}");
                        firstFailure ??= e;
                        failedReads++;
                        continue;
                    }
                }

                TryEvict(entry, finalizedNonce);
            }

            if (firstFailure is not null && _logger.IsWarn) _logger.Warn($"Could not read the finalized nonce of {failedReads} address(es); keeping their nonce managers. {firstFailure.Message}");
        }
        catch (MissingTrieNodeException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Finalized state is no longer available; skipping the rest of the nonce eviction sweep. {e.Message}");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Unexpected error in the nonce eviction sweep", e);
        }
    }

    private void TryEvict(KeyValuePair<AddressAsKey, AddressNonceManager> entry, ulong? finalizedNonce)
    {
        if (entry.Value.TryMarkEvicted(finalizedNonce))
        {
            _addressNonceManagers.TryRemove(entry);
            entry.Value.Unlock();
        }
    }

    private class AddressNonceManager
    {
        private readonly HashSet<ulong> _usedNonces = [];
        private ulong _currentNonce;
        private ulong _reservedNonce;
        private ulong _previousAccountNonce;

        // Never disposed: a holder that lost the race to eviction still releases it after seeing the tombstone.
        private readonly SemaphoreSlim _accountLock = new(1);

        public bool IsEvicted { get; private set; }

        public int UsedNonceCount => _usedNonces.Count;

        /// <summary>Unlocked pre-check; <see cref="TryMarkEvicted"/> re-checks under the lock.</summary>
        public bool IsUnused => _usedNonces.Count == 0 && _currentNonce == 0;

        public NonceLocker Lock() => new(_accountLock, TxAccepted);

        public void Unlock() => _accountLock.Release();

        public ulong ReserveNonce(ulong accountNonce)
        {
            ReleaseNonces(accountNonce);
            _currentNonce = ulong.Max(_currentNonce, accountNonce);
            _reservedNonce = _currentNonce;
            return _currentNonce;
        }

        private void TxAccepted()
        {
            _usedNonces.Add(_reservedNonce);
            while (_usedNonces.Contains(_currentNonce))
            {
                _currentNonce++;
            }
        }

        public void TxWithNonceReceived(ulong accountNonce, ulong nonce)
        {
            ReleaseNonces(accountNonce);
            _reservedNonce = nonce;
        }

        /// <summary>
        /// Tombstones the manager when it holds nothing a fresh one would not, leaving it locked for the caller
        /// to remove and then <see cref="Unlock"/>.
        /// </summary>
        /// <param name="accountNonce">The account's finalized nonce, or <c>null</c> when it is unknown, in which case
        /// only a manager that never handed out a nonce qualifies.</param>
        /// <remarks>
        /// Skips a manager whose lock is held rather than waiting for it, so a sweep never blocks on a sender.
        /// </remarks>
        public bool TryMarkEvicted(ulong? accountNonce)
        {
            if (!_accountLock.Wait(0)) return false;

            if (accountNonce is not null) ReleaseNonces(accountNonce.Value);
            if (_usedNonces.Count == 0 && _currentNonce <= accountNonce.GetValueOrDefault())
            {
                IsEvicted = true;
                return true;
            }

            _accountLock.Release();
            return false;
        }

        private void ReleaseNonces(ulong accountNonce)
        {
            // Stops once the set is empty, so a fresh manager does not walk every nonce below the account's.
            for (ulong i = _previousAccountNonce; i < accountNonce && _usedNonces.Count != 0; i++)
            {
                _usedNonces.Remove(i);
            }

            _previousAccountNonce = accountNonce;
        }
    }
}
