// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private readonly ConcurrentDictionary<Address, TxPool.AddressNonces> _nonces;
        private readonly ILogger _logger;

        public ReusedOwnNonceTxFilter(ConcurrentDictionary<Address, TxPool.AddressNonces> nonces, ILogger logger)
        {
            _nonces = nonces;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            bool managedNonce = (handlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
            Account account = state.SenderAccount;
            UInt256 currentNonce = account.Nonce;

            if (managedNonce && CheckOwnTransactionAlreadyUsed(tx, currentNonce))
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce already used.");
                return AcceptTxResult.OwnNonceAlreadyUsed;
            }

            return AcceptTxResult.Accepted;
        }

        /// <summary>
        /// Nonce manager needed
        /// </summary>
        /// <param name="transaction"></param>
        /// <param name="currentNonce"></param>
        /// <returns></returns>
        private bool CheckOwnTransactionAlreadyUsed(Transaction transaction, in UInt256 currentNonce)
        {
            Address address = transaction.SenderAddress!; // since unknownSenderFilter will run before this one
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
