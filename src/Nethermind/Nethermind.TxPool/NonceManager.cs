// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.TxPool;

public class NonceManager(IAccountStateProvider accounts) : INonceManager
{
    private readonly ConcurrentDictionary<AddressAsKey, AddressNonceManager> _addressNonceManagers = new();
    private readonly IAccountStateProvider _accounts = accounts;

    public NonceLocker ReserveNonce(Address address, ITxPool txPool, out ulong reservedNonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, static _ => new AddressNonceManager());
        return addressNonceManager.ReserveNonce(address, _accounts, txPool, out reservedNonce);
    }

    public NonceLocker TxWithNonceReceived(Address address, ulong nonce)
    {
        AddressNonceManager addressNonceManager =
            _addressNonceManagers.GetOrAdd(address, static _ => new AddressNonceManager());
        return addressNonceManager.TxWithNonceReceived(nonce);
    }

    private class AddressNonceManager
    {
        private readonly Dictionary<ulong, (Hash256 Hash, TxType Type)> _usedNonces = [];
        private ulong _nextUnusedNonce;
        private ulong _reservedNonce;
        private ulong _previousAccountNonce;

        private readonly SemaphoreSlim _accountLock = new(1);

        /// <remarks>
        /// Rederived from the account nonce on every call, so a nonce whose transaction has since left the pool is
        /// handed out again. The account lock is held until the locker is disposed, so there is never more than one
        /// reservation in flight and an accepted one is recorded before the next starts. The account nonce is read
        /// only once the lock is held, since a block may consume the previous floor while this call waits for it.
        /// </remarks>
        public NonceLocker ReserveNonce(Address address, IAccountStateProvider accounts, ITxPool txPool, out ulong reservedNonce)
        {
            NonceLocker locker = new(_accountLock, TxAccepted);
            try
            {
                ulong accountNonce = accounts.GetNonce(address);
                ReleaseNonces(accountNonce);
                _reservedNonce = FindFreeNonce(address, accountNonce, txPool);
            }
            catch
            {
                locker.Dispose();
                throw;
            }

            reservedNonce = _reservedNonce;
            return locker;
        }

        private void TxAccepted(Transaction transaction)
        {
            _usedNonces[_reservedNonce] = (transaction.Hash!, transaction.Type);
            _nextUnusedNonce = ulong.Max(_nextUnusedNonce, _reservedNonce + 1);
        }

        /// <remarks>
        /// A nonce at or above <see cref="_nextUnusedNonce"/> was never accepted here, so it is free without asking the
        /// pool. Below it, a nonce is taken while the pool still holds its recorded transaction (the persistent
        /// broadcaster included) or any pending account-domain transaction of the sender at that nonce, which covers
        /// replacements and transactions returned to the pool by a reorg. The pool snapshots are taken only when
        /// the recorded transaction is gone, and the holder found there becomes the recorded one.
        /// </remarks>
        private ulong FindFreeNonce(Address address, ulong accountNonce, ITxPool txPool)
        {
            Transaction[]? pending = null;
            Transaction[]? pendingBlobs = null;
            ulong nonce = accountNonce;
            for (; nonce < _nextUnusedNonce; nonce++)
            {
                bool recorded = _usedNonces.TryGetValue(nonce, out (Hash256 Hash, TxType Type) used);
                if (recorded && txPool.ContainsTx(used.Hash, used.Type))
                {
                    continue;
                }

                pending ??= txPool.GetPendingTransactionsBySender(address);
                pendingBlobs ??= txPool.GetPendingLightBlobTransactionsBySender(address);
                Transaction? holder = FindAccountNonceHolder(pending, nonce) ?? FindAccountNonceHolder(pendingBlobs, nonce);
                if (holder is null)
                {
                    if (recorded)
                    {
                        _usedNonces.Remove(nonce);
                    }

                    return nonce;
                }

                _usedNonces[nonce] = (holder.Hash!, holder.Type);
            }

            return nonce;
        }

        private static Transaction? FindAccountNonceHolder(Transaction[] transactions, ulong nonce)
        {
            foreach (Transaction transaction in transactions)
            {
                if (transaction.Nonce == nonce && !KeyedNonceManager.UsesKeyedNonce(transaction))
                {
                    return transaction;
                }
            }

            return null;
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
