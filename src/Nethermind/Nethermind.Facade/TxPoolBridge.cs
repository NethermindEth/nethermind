//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Facade
{
    public class TxPoolBridge : ITxPoolBridge
    {
        private readonly ITxPool _txPool;
        private readonly IWallet _wallet;
        private readonly ITimestamper _timestamper;
        private readonly int _chainId;

        /// <summary>
        /// </summary>
        /// <param name="txPool">TX pool / mempool that stores all pending transactions.</param>
        /// <param name="wallet">Wallet for new transactions signing</param>
        /// <param name="timestamper">Timestamper for stamping the arrinving transactions.</param>
        /// <param name="chainId">Chain ID to signing transactions for.</param>
        public TxPoolBridge(ITxPool txPool, IWallet wallet, ITimestamper timestamper, int chainId)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _chainId = chainId;
        }

        public Transaction GetPendingTransaction(Keccak txHash)
        {
            _txPool.TryGetPendingTransaction(txHash, out var transaction);
            return transaction;
        }

        public Transaction[] GetPendingTransactions() => _txPool.GetPendingTransactions();

        public Keccak SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            if (tx.Signature == null)
            {
                if (_wallet.IsUnlocked(tx.SenderAddress))
                {
                    Sign(tx);
                }
                else
                {
                    throw new SecurityException("Your account is locked. Unlock the account via CLI, personal_unlockAccount or use Trusted Signer.");
                }
            }

            tx.Hash = tx.CalculateHash();
            tx.Timestamp = _timestamper.EpochSeconds;

            AddTxResult result = _txPool.AddTransaction(tx, txHandlingOptions);

            if (result == AddTxResult.OwnNonceAlreadyUsed && (txHandlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce)
            {
                // below the temporary NDM support - needs some review
                tx.Nonce = _txPool.ReserveOwnTransactionNonce(tx.SenderAddress);
                Sign(tx);
                tx.Hash = tx.CalculateHash();
                _txPool.AddTransaction(tx, txHandlingOptions);
            }

            return tx.Hash;
        }
        
        private void Sign(Transaction tx)
        {
            _wallet.Sign(tx, _chainId);
        }
    }
}