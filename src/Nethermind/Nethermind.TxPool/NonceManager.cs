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

    public NonceLocker ReserveNonce(Address address)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, _ => new AddressNonceManager());
        return addressNonceManager.ReserveNonce(_accounts.GetAccount(address).Nonce);
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
        private UInt256 _previousAccountNonce;

        private readonly SemaphoreSlim _accountLock = new(1);

        public NonceLocker ReserveNonce(UInt256 accountNonce)
        {
            NonceLocker locker = new(_accountLock,
                () => _currentNonce = UInt256.Max(_currentNonce, accountNonce),
                TxAccepted);
            ReleaseNonces(accountNonce);

            return locker;
        }

        private void TxAccepted(UInt256 reservedNonce)
        {
            _usedNonces.Add(reservedNonce);
            while (_usedNonces.Contains(_currentNonce))
            {
                _currentNonce++;
            }
        }

        public NonceLocker TxWithNonceReceived(UInt256 nonce) => new(_accountLock, () => nonce, TxAccepted);

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
