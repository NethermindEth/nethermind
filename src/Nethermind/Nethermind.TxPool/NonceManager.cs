// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    public UInt256 ReserveNonce(Address address)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, _ => new AddressNonceManager());
        return addressNonceManager.ReserveNonce(_accounts.GetAccount(address).Nonce);
    }

    public void TxAccepted(Address address)
    {
        if (_addressNonceManagers.TryGetValue(address, out AddressNonceManager? addressNonceManager))
        {
            addressNonceManager.TxAccepted();
        }
    }

    public void TxRejected(Address address)
    {
        if (_addressNonceManagers.TryGetValue(address, out AddressNonceManager? addressNonceManager))
        {
            addressNonceManager.TxRejected();
        }
    }

    public void TxWithNonceReceived(Address address, UInt256 nonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, _ => new AddressNonceManager());
        addressNonceManager.TxWithNonceReceived(nonce);
    }

    private class AddressNonceManager
    {
        private HashSet<UInt256> _usedNonces = new();
        private UInt256 _reservedNonce;
        private UInt256 _currentNonce;
        private UInt256 _previousAccountNonce;
        private readonly Mutex _mutex = new();

        public UInt256 ReserveNonce(UInt256 accountNonce)
        {
            _mutex.WaitOne();

            ReleaseNonces(accountNonce);
            _currentNonce = UInt256.Max(_currentNonce, accountNonce);
            _reservedNonce = _currentNonce;
            return _currentNonce;
        }

        public void TxAccepted()
        {
            _usedNonces.Add(_reservedNonce);
            while (_usedNonces.Contains(_currentNonce))
            {
                _currentNonce++;
            }

            _mutex.ReleaseMutex();
        }

        public void TxWithNonceReceived(UInt256 nonce)
        {
            _mutex.WaitOne();
            _reservedNonce = nonce;
        }

        private void ReleaseNonces(UInt256 accountNonce)
        {
            for (UInt256 i = _previousAccountNonce; i < accountNonce; i++)
            {
                _usedNonces.Remove(i);
            }

            _previousAccountNonce = accountNonce;
        }

        public void TxRejected() => _mutex.ReleaseMutex();
    }
}
