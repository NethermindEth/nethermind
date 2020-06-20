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
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Facade.Transactions;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Facade
{
    public class TxPoolBridge : ITxPoolBridge
    {
        private readonly ITxPool _txPool;
        private readonly ITxSender _txSender;

        /// <summary>
        /// </summary>
        /// <param name="txPool">TX pool / mempool that stores all pending transactions.</param>
        /// <param name="txSigner">Transaction signer</param>
        /// <param name="timestamper">Timestamper for stamping the arriving transactions.</param>
        public TxPoolBridge(ITxPool txPool, ITxSigner txSigner, ITimestamper timestamper) 
            : this(txPool, new TxPoolSender(txPool, txSigner, timestamper))
        {
        }

        public TxPoolBridge(ITxPool txPool, ITxSender txSender)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
        }

        public Transaction GetPendingTransaction(Keccak txHash)
        {
            _txPool.TryGetPendingTransaction(txHash, out var transaction);
            return transaction;
        }

        public Transaction[] GetPendingTransactions() => _txPool.GetPendingTransactions();

        public Keccak SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            try
            {
                return _txSender.SendTransaction(tx, txHandlingOptions);
            }
            catch (SecurityException e)
            {
                throw new SecurityException("Your account is locked. Unlock the account via CLI, personal_unlockAccount or use Trusted Signer.", e);
            }
        }
    }
}
