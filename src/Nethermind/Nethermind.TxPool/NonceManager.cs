// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public class NonceManager : INonceManager
{
    private readonly ConcurrentDictionary<Address, AddressNonceManager> _addressNonceManagers = new();
    private readonly IAccountStateProvider _accounts;

    public NonceManager(IAccountStateProvider accounts)
    {
        _accounts = accounts;
    }

    public IDisposable ReserveNonce(Address address, out UInt256 nonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, _ => new AddressNonceManager());
        return addressNonceManager.ReserveNonce(_accounts.GetAccount(address).Nonce, out nonce);
    }

    public void TxAccepted(Address address)
    {
        if (_addressNonceManagers.TryGetValue(address, out AddressNonceManager? addressNonceManager))
        {
            addressNonceManager.TxAccepted();
        }
    }

    public IDisposable TxWithNonceReceived(Address address, UInt256 nonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, _ => new AddressNonceManager());
        return addressNonceManager.TxWithNonceReceived(nonce);
    }

    private class AddressNonceManager
    {
        private readonly HashSet<UInt256> _usedNonces = new();
        private UInt256 _reservedNonce;
        private UInt256 _currentNonce;
        private UInt256 _previousAccountNonce;

        private readonly SemaphoreSlim _accountLock = new(1);

        public IDisposable ReserveNonce(UInt256 accountNonce, out UInt256 nonce)
        {
            IDisposable locker = new AccountLocker(_accountLock);
            ReleaseNonces(accountNonce);
            _currentNonce = UInt256.Max(_currentNonce, accountNonce);
            _reservedNonce = _currentNonce;
            nonce = _currentNonce;

            return locker;
        }

        public void TxAccepted()
        {
            _usedNonces.Add(_reservedNonce);
            while (_usedNonces.Contains(_currentNonce))
            {
                _currentNonce++;
            }
        }

        public IDisposable TxWithNonceReceived(UInt256 nonce)
        {
            IDisposable locker = new AccountLocker(_accountLock);
            _reservedNonce = nonce;
            return locker;
        }

        private void ReleaseNonces(UInt256 accountNonce)
        {
            for (UInt256 i = _previousAccountNonce; i < accountNonce; i++)
            {
                _usedNonces.Remove(i);
            }

            _previousAccountNonce = accountNonce;
        }
    }

    private class AccountLocker : IDisposable
    {
        private readonly SemaphoreSlim _accountLock;
        private int _disposed;

        public AccountLocker(SemaphoreSlim accountLock)
        {
            _accountLock = accountLock;
            _accountLock.Wait();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _accountLock.Release();
                }
            }
        }

        ~AccountLocker()
        {
            Dispose(true);
        }
    }
}
