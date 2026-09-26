// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;
using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.TxPool;

public class NonceManager(IAccountStateProvider accounts, int minSweepThreshold = NonceManager.DefaultMinSweepThreshold) : INonceManager
{
    internal const int DefaultMinSweepThreshold = 1024;

    private readonly ConcurrentDictionary<AddressAsKey, AddressNonceManager> _addressNonceManagers = new();
    private readonly IAccountStateProvider _accounts = accounts;
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
    /// Drops the addresses whose every recorded nonce is already below the account nonce: such an entry decides
    /// nothing a fresh one would not. Busy entries are skipped rather than waited for.
    /// </summary>
    /// <remarks>
    /// Runs on the submitting thread and reads one account nonce per tracked address. The threshold doubles after
    /// each sweep, so the cost stays constant per submission on average, but a single submission can pay for a scan.
    /// </remarks>
    private void SweepConfirmed()
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return;

        try
        {
            foreach (KeyValuePair<AddressAsKey, AddressNonceManager> entry in _addressNonceManagers)
            {
                if (entry.Value.TryRetire(_accounts.GetNonce(entry.Key)))
                {
                    _addressNonceManagers.TryRemove(entry);
                }
            }

            Volatile.Write(ref _sweepThreshold, Math.Max(_minSweepThreshold, _addressNonceManagers.Count * 2));
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    private class AddressNonceManager
    {
        private readonly HashSet<ulong> _usedNonces = [];
        private readonly Action _txAccepted;
        private ulong _currentNonce;
        private ulong _reservedNonce;
        private ulong _previousAccountNonce;
        private volatile bool _isRetired;

        private readonly SemaphoreSlim _accountLock = new(1);

        public AddressNonceManager() => _txAccepted = TxAccepted;

        public bool IsRetired => _isRetired;

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

        public bool TryRetire(ulong accountNonce)
        {
            if (!_accountLock.Wait(0)) return false;

            try
            {
                if (_currentNonce > accountNonce) return false;

                foreach (ulong usedNonce in _usedNonces)
                {
                    if (usedNonce >= accountNonce) return false;
                }

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
                    _usedNonces.RemoveWhere(nonce => nonce >= previousAccountNonce && nonce < accountNonce);
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
