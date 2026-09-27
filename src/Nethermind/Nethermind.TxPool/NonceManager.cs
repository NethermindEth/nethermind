// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.TxPool;

public class NonceManager(IAccountStateProvider accounts) : INonceManager
{
    private readonly ConcurrentDictionary<AddressAsKey, AddressNonceManager> _addressNonceManagers = new();
    private readonly IAccountStateProvider _accounts = accounts;

    public NonceLocker ReserveNonce(Address address, IPendingTxsBySender pendingTxs, out ulong reservedNonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, static _ => new AddressNonceManager());
        return addressNonceManager.ReserveNonce(address, _accounts.GetNonce(address), pendingTxs, out reservedNonce);
    }

    public NonceLocker TxWithNonceReceived(Address address, ulong nonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, static _ => new AddressNonceManager());
        return addressNonceManager.TxWithNonceReceived(nonce);
    }

    private class AddressNonceManager
    {
        private readonly HashSet<ulong> _usedNonces = [];
        private ulong _currentNonce;
        private ulong _reservedNonce;
        private ulong _previousAccountNonce;

        private readonly SemaphoreSlim _accountLock = new(1);

        public NonceLocker ReserveNonce(Address address, ulong accountNonce, IPendingTxsBySender pendingTxs, out ulong reservedNonce)
        {
            NonceLocker locker = new(_accountLock, TxAccepted);
            ReleaseNonces(accountNonce);
            _currentNonce = ulong.Max(_currentNonce, accountNonce);
            _reservedNonce = FirstNonceNotPending(address, pendingTxs);
            reservedNonce = _reservedNonce;
            return locker;
        }

        private void TxAccepted()
        {
            _usedNonces.Add(_reservedNonce);
            SkipUsedNonces();
        }

        private ulong FirstNonceNotPending(Address address, IPendingTxsBySender pendingTxs)
        {
            ulong nonce = _currentNonce;
            if (!_usedNonces.Contains(nonce))
            {
                return nonce;
            }

            Transaction[] pending = pendingTxs.GetPendingTransactionsBySender(address);
            Transaction[] pendingBlobs = pendingTxs.GetPendingLightBlobTransactionsBySender(address);
            while (_usedNonces.Contains(nonce))
            {
                if (!HoldsAccountNonce(pending, nonce) && !HoldsAccountNonce(pendingBlobs, nonce))
                {
                    _usedNonces.Remove(nonce);
                    return nonce;
                }

                nonce++;
            }

            return nonce;
        }

        private static bool HoldsAccountNonce(Transaction[] transactions, ulong nonce)
        {
            foreach (Transaction transaction in transactions)
            {
                if (transaction.Nonce == nonce && !KeyedNonceManager.UsesKeyedNonce(transaction))
                {
                    return true;
                }
            }

            return false;
        }

        private void SkipUsedNonces()
        {
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
