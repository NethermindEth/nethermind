// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;
using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.TxPool;

public class NonceManager(
    IChainHeadInfoProvider chainHeadInfoProvider,
    IStateHeaderProvider stateHeaderProvider,
    IStateReader stateReader,
    int minSweepThreshold = NonceManager.DefaultMinSweepThreshold) : INonceManager
{
    internal const int DefaultMinSweepThreshold = 1024;

    private readonly ConcurrentDictionary<AddressAsKey, AddressNonceManager> _addressNonceManagers = new();
    private readonly IAccountStateProvider _accounts = chainHeadInfoProvider.ReadOnlyStateProvider;
    private readonly IChainHeadInfoProvider _chainHeadInfoProvider = chainHeadInfoProvider;
    private readonly IStateHeaderProvider _stateHeaderProvider = stateHeaderProvider;
    private readonly IStateReader _stateReader = stateReader;
    private readonly int _minSweepThreshold = minSweepThreshold;
    private int _sweepThreshold = minSweepThreshold;
    private int _sweeping;

    internal int TrackedAddressCount => _addressNonceManagers.Count;

    public NonceLocker ReserveNonce(Address address, out ulong reservedNonce)
    {
        while (true)
        {
            AddressNonceManager addressNonceManager = GetAddressNonceManager(address);
            NonceLocker locker = addressNonceManager.ReserveNonce(_accounts.GetNonce(address), out reservedNonce);
            if (!addressNonceManager.IsRetired) return locker;
            locker.Dispose();
        }
    }

    public NonceLocker TxWithNonceReceived(Address address, ulong nonce)
    {
        while (true)
        {
            AddressNonceManager addressNonceManager = GetAddressNonceManager(address);
            NonceLocker locker = addressNonceManager.TxWithNonceReceived(nonce);
            if (!addressNonceManager.IsRetired) return locker;
            locker.Dispose();
        }
    }

    private AddressNonceManager GetAddressNonceManager(Address address)
    {
        if (_addressNonceManagers.Count >= Volatile.Read(ref _sweepThreshold))
        {
            SweepConfirmed();
        }

        return _addressNonceManagers.GetOrAdd(address, static _ => new AddressNonceManager());
    }

    /// <summary>
    /// Drops the addresses whose every accepted nonce is below their nonce at the reorg-safe block, and the addresses
    /// that never had a nonce accepted. A reorg that keeps the reorg-safe block cannot lower the account nonce below
    /// what it was there, so a fresh entry never hands out a nonce the dropped one did. Busy entries are skipped
    /// rather than waited for.
    /// </summary>
    /// <remarks>
    /// Runs on the submitting thread. Worst case, one request pays one reorg-safe state read per tracked entry that had
    /// a nonce accepted. The threshold doubles after each sweep, even one that throws, so that cost is amortized across
    /// submissions. Without state at the reorg-safe block only the never-accepted entries go.
    /// </remarks>
    private void SweepConfirmed()
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return;

        try
        {
            BlockHeader? reorgSafeHeader = FindReorgSafeHeader();
            foreach (KeyValuePair<AddressAsKey, AddressNonceManager> entry in _addressNonceManagers)
            {
                ulong? reorgSafeNonce = reorgSafeHeader is not null && entry.Value.HasAcceptedNonce
                    ? GetReorgSafeNonce(reorgSafeHeader, entry.Key)
                    : null;
                if (entry.Value.TryRetire(reorgSafeNonce))
                {
                    _addressNonceManagers.TryRemove(entry);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _sweepThreshold, Math.Max(_minSweepThreshold, _addressNonceManagers.Count * 2));
            Volatile.Write(ref _sweeping, 0);
        }
    }

    /// <summary>
    /// The canonical header at <c>min(finalized, head - </c><see cref="Reorganization.MaxDepth"/><c>)</c>, or
    /// <c>null</c> when it or its state is unavailable.
    /// </summary>
    private BlockHeader? FindReorgSafeHeader()
    {
        ulong reorgSafeNumber = ulong.Min(
            _stateHeaderProvider.FinalizedBlockNumber,
            _chainHeadInfoProvider.HeadNumber.SaturatingSub(Reorganization.MaxDepth));
        BlockHeader? header = _stateHeaderProvider.GetFinalizedHeader(reorgSafeNumber);
        return header is not null && _stateReader.HasStateForBlock(header) ? header : null;
    }

    private ulong? GetReorgSafeNonce(BlockHeader reorgSafeHeader, Address address)
    {
        try
        {
            return _stateReader.GetNonce(reorgSafeHeader, address);
        }
        catch (MissingTrieNodeException)
        {
            // Pruned after FindReorgSafeHeader looked, which counts as unavailable, not as nonce 0.
            return null;
        }
    }

    private class AddressNonceManager
    {
        private readonly HashSet<ulong> _usedNonces = [];
        private readonly Action _txAccepted;
        private ulong _currentNonce;
        private ulong _reservedNonce;
        private ulong _previousAccountNonce;
        private ulong? _highestAcceptedNonce;
        private volatile bool _isRetired;

        private readonly SemaphoreSlim _accountLock = new(1);

        public AddressNonceManager() => _txAccepted = TxAccepted;

        public bool IsRetired => _isRetired;

        public bool HasAcceptedNonce => _highestAcceptedNonce.HasValue;

        public NonceLocker ReserveNonce(ulong accountNonce, out ulong reservedNonce)
        {
            NonceLocker locker = new(_accountLock, _txAccepted);
            ReleaseNonces(accountNonce);
            _currentNonce = ulong.Max(_currentNonce, accountNonce);
            _reservedNonce = _currentNonce;
            reservedNonce = _currentNonce;
            return locker;
        }

        private void TxAccepted()
        {
            _usedNonces.Add(_reservedNonce);
            _highestAcceptedNonce = ulong.Max(_highestAcceptedNonce ?? 0, _reservedNonce);
            while (_usedNonces.Contains(_currentNonce))
            {
                _currentNonce++;
            }
        }

        public NonceLocker TxWithNonceReceived(ulong nonce)
        {
            NonceLocker locker = new(_accountLock, _txAccepted);
            _reservedNonce = nonce;
            return locker;
        }

        /// <param name="reorgSafeNonce">
        /// The account nonce at the reorg-safe block, or <c>null</c> when that state is unavailable.
        /// </param>
        public bool TryRetire(ulong? reorgSafeNonce)
        {
            if (!_accountLock.Wait(0)) return false;

            try
            {
                bool everyAcceptedNonceIsReorgSafe = _highestAcceptedNonce is null || _highestAcceptedNonce < reorgSafeNonce;
                if (!everyAcceptedNonceIsReorgSafe) return false;

                _isRetired = true;
                return true;
            }
            finally
            {
                _accountLock.Release();
            }
        }

        private void ReleaseNonces(ulong accountNonce)
        {
            if (accountNonce > _previousAccountNonce)
            {
                if ((ulong)_usedNonces.Count < accountNonce - _previousAccountNonce)
                {
                    ulong previousAccountNonce = _previousAccountNonce;
                    ulong releasedBelow = accountNonce;
                    _usedNonces.RemoveWhere(nonce => nonce >= previousAccountNonce && nonce < releasedBelow);
                }
                else
                {
                    for (ulong i = _previousAccountNonce; i < accountNonce; i++)
                    {
                        _usedNonces.Remove(i);
                    }
                }
            }

            _previousAccountNonce = accountNonce;
        }
    }
}
