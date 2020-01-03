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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class PendingTransactionSelector : IPendingTransactionSelector
    {
        private readonly ITxPool _transactionPool;
        private readonly IStateProvider _stateProvider;
        private readonly long _minGasPriceForMining;
        private readonly ILogger _logger;

        public PendingTransactionSelector(ITxPool transactionPool, IStateProvider stateProvider, ILogManager logManager, long minGasPriceForMining = 1)
        {
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _logger = logManager?.GetClassLogger<PendingTransactionSelector>() ?? throw new ArgumentNullException(nameof(logManager));
            _minGasPriceForMining = minGasPriceForMining;
        }
        
        public IEnumerable<Transaction> SelectTransactions(long gasLimit)
        {
            UInt256 GetCurrentNonce(IDictionary<Address, UInt256> noncesDictionary, Address address)
            {
                if (!noncesDictionary.TryGetValue(address, out var nonce))
                {
                    noncesDictionary[address] = nonce = _stateProvider.GetNonce(address);
                }
                
                return nonce;
            }
            
            UInt256 GetRemainingBalance(IDictionary<Address, UInt256> balances, Address address)
            {
                if (!balances.TryGetValue(address, out var balance))
                {
                    balances[address] = balance = _stateProvider.GetBalance(address);
                }
                
                return balance;
            }
            
            bool HasEnoughFounds(IDictionary<Address, UInt256> balances, Transaction transaction)
            {
                var balance = GetRemainingBalance(balances, transaction.SenderAddress);
                var transactionPotentialCost = transaction.GasPrice * (ulong) transaction.GasLimit + transaction.Value;
                
                if (balance < transactionPotentialCost)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - transaction cost ({transactionPotentialCost}) is higher than sender balance ({balance}).");
                    return false;
                }
                else
                {
                    balances[transaction.SenderAddress] = balance - transactionPotentialCost;
                    return true;
                }
            }

            var pendingTransactions = _transactionPool.GetPendingTransactions();
            var transactions = pendingTransactions.OrderBy(t => t.Nonce).ThenByDescending(t => t.GasPrice).ThenBy(t => t.GasLimit);
            IDictionary<Address, UInt256> remainingBalance = new Dictionary<Address, UInt256>();
            Dictionary<Address, UInt256> nonces = new Dictionary<Address, UInt256>();
            List<Transaction> selected = new List<Transaction>();
            long gasRemaining = gasLimit;

            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at min gas price {_minGasPriceForMining} and block gas limit {gasRemaining}.");

            foreach (Transaction transaction in transactions)
            {
                if (transaction.SenderAddress == null)
                {
                    if (_logger.IsTrace) _logger.Trace("Rejecting null sender pending transaction.");
                    continue;
                }
                
                if (transaction.GasPrice < _minGasPriceForMining)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - gas price ({transaction.GasPrice}) too low (min gas price: {_minGasPriceForMining}.");
                    continue;
                }

                if (GetCurrentNonce(nonces, transaction.SenderAddress) != transaction.Nonce)  
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction based on nonce.");
                    continue;
                }

                if (transaction.GasLimit > gasRemaining)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - gas limit ({transaction.GasLimit}) more than remaining gas ({gasRemaining}).");
                    break;
                }
                
                if (!HasEnoughFounds(remainingBalance, transaction))
                {
                    continue;
                }

                selected.Add(transaction);
                nonces[transaction.SenderAddress] = transaction.Nonce + 1;
                gasRemaining -= transaction.GasLimit;
            }
            
            if (_logger.IsDebug) _logger.Debug($"Collected {selected.Count} out of {pendingTransactions.Length} pending transactions.");

            return selected;
        }
    }
}