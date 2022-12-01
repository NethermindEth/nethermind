// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
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
            _addressNonceManagers.GetOrAdd(address, v => new AddressNonceManager(_accounts.GetAccount(v).Nonce));
        return addressNonceManager.ReserveNonce();
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

    private class AddressNonceManager
    {
        private UInt256 _currentNonce;
        private static Mutex mutex = new();

        public AddressNonceManager(UInt256 startNonce)
        {
            _currentNonce = startNonce;
        }

        public UInt256 ReserveNonce()
        {
            mutex.WaitOne();
            return _currentNonce;
        }

        public void TxAccepted()
        {
            _currentNonce += 1;
            mutex.ReleaseMutex();
        }

        public void TxRejected() => mutex.ReleaseMutex();
    }
}
