// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.TxPool;

public class NonceManager : INonceManager
{
    private readonly ConcurrentDictionary<AddressAsKey, AddressNonceManager> _addressNonceManagers = new();
    private readonly IAccountStateProvider _accounts;

    public NonceManager(IAccountStateProvider accounts)
    {
        _accounts = accounts;
    }

    public NonceLocker ReserveNonce(Address address, out ulong reservedNonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, static _ => new AddressNonceManager());
        ulong accountNonce = _accounts.GetNonce(address).ToUInt64(null);
        return addressNonceManager.ReserveNonce(accountNonce, out reservedNonce);
    }

    public NonceLocker TxWithNonceReceived(Address address, ulong nonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, static _ => new AddressNonceManager());
        return addressNonceManager.TxWithNonceReceived(nonce);
    }

    private class AddressNonceManager
    {
        private readonly HashSet<ulong> _usedNonces = new();
        private ulong _currentNonce;
        private ulong _reservedNonce;
        private ulong _previousAccountNonce;

        private readonly SemaphoreSlim _accountLock = new(1);

        public NonceLocker ReserveNonce(ulong accountNonce, out ulong reservedNonce)
        {
            NonceLocker locker = new(_accountLock, TxAccepted);
            ReleaseNonces(accountNonce);
            _currentNonce = _currentNonce > accountNonce ? _currentNonce : accountNonce;
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
            NonceLocker locker = new(_accountLock, TxAccepted);
            _reservedNonce = nonce;
            return locker;
        }

        private void ReleaseNonces(ulong accountNonce)
        {
            for (ulong i = _previousAccountNonce; i < accountNonce; i++)
            {
                _usedNonces.Remove(i);
            }

            _previousAccountNonce = accountNonce;
        }
    }
}
