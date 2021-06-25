//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that were generated at this machine and were already signed with the same nonce.
    /// TODO: review if cancel by replace is still possible with this!
    /// </summary>
    internal class ReusedOwnNonceTxFilter : IIncomingTxFilter
    {
        private readonly object _locker = new();
        private readonly IAccountStateProvider _accounts;
        private readonly ConcurrentDictionary<Address, TxPool.AddressNonces> _nonces;
        private readonly ILogger _logger;

        public ReusedOwnNonceTxFilter(IAccountStateProvider accountStateProvider, ConcurrentDictionary<Address, TxPool.AddressNonces> nonces, ILogger logger)
        {
            _accounts = accountStateProvider;
            _nonces = nonces;
            _logger = logger;
        }
            
        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions handlingOptions)
        {
            bool managedNonce = (handlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
            Account account = _accounts.GetAccount(tx.SenderAddress!);
            UInt256 currentNonce = account.Nonce;
            
            if (managedNonce && CheckOwnTransactionAlreadyUsed(tx, currentNonce))
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce already used.");
                return (false, AddTxResult.OwnNonceAlreadyUsed);
            }

            return (true, null);
        }
        
        /// <summary>
        /// Nonce manager needed
        /// </summary>
        /// <param name="transaction"></param>
        /// <param name="currentNonce"></param>
        /// <returns></returns>
        private bool CheckOwnTransactionAlreadyUsed(Transaction transaction, UInt256 currentNonce)
        {
            Address address = transaction.SenderAddress;
            lock (_locker)
            {
                if (!_nonces.TryGetValue(address, out var addressNonces))
                {
                    addressNonces = new TxPool.AddressNonces(currentNonce);
                    _nonces.TryAdd(address, addressNonces);
                }

                if (!addressNonces.Nonces.TryGetValue(transaction.Nonce, out var nonce))
                {
                    nonce = new TxPool.NonceInfo(transaction.Nonce);
                    addressNonces.Nonces.TryAdd(transaction.Nonce, new TxPool.NonceInfo(transaction.Nonce));
                }

                if (!(nonce.TransactionHash is null && nonce.TransactionHash != transaction.Hash))
                {
                    // Nonce conflict
                    if (_logger.IsDebug)
                        _logger.Debug(
                            $"Nonce: {nonce.Value} was already used in transaction: '{nonce.TransactionHash}' and cannot be reused by transaction: '{transaction.Hash}'.");

                    return true;
                }

                nonce.SetTransactionHash(transaction.Hash!);
            }

            return false;
        }
    }
}
