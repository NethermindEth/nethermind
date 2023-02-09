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

    public NonceLocker ReserveNonce(Address address, out UInt256 reservedNonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, _ => new AddressNonceManager());
        return addressNonceManager.ReserveNonce(_accounts.GetAccount(address).Nonce, out reservedNonce);
    }

    public NonceLocker TxWithNonceReceived(Address address, UInt256 nonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, _ => new AddressNonceManager());
        return addressNonceManager.TxWithNonceReceived(nonce);
    }

    private class AddressNonceManager
    {
        private readonly HashSet<UInt256> _usedNonces = new();
        private UInt256 _currentNonce;
        private UInt256 _reservedNonce;
        private UInt256 _previousAccountNonce;

        private readonly SemaphoreSlim _accountLock = new(1);

        public NonceLocker ReserveNonce(UInt256 accountNonce, out UInt256 reservedNonce)
        {
            NonceLocker locker = new(_accountLock, TxAccepted);
            ReleaseNonces(accountNonce);
            _currentNonce = UInt256.Max(_currentNonce, accountNonce);
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

        public NonceLocker TxWithNonceReceived(UInt256 nonce)
        {
            _reservedNonce = nonce;
            return new(_accountLock, TxAccepted);
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
}
