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
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Producers
{
    public class TxPoolTxSource : ITxSource
    {
        private readonly ITxPool _transactionPool;
        private readonly IStateReader _stateReader;
        private readonly ILogger _logger;
        private readonly long _minGasPriceForMining;

        public TxPoolTxSource(ITxPool transactionPool, IStateReader stateReader, ILogManager logManager, long minGasPriceForMining = 0)
        {
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _logger = logManager?.GetClassLogger<TxPoolTxSource>() ?? throw new ArgumentNullException(nameof(logManager));
            _minGasPriceForMining = minGasPriceForMining;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            T GetFromState<T>(Func<Keccak, Address, T> stateGetter, Address address, T defaultValue)
            {
                T value = defaultValue;
                try
                {
                    value = stateGetter(parent.StateRoot, address);
                }
                catch (TrieException e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Couldn't get state for address {address}.{Environment.NewLine}{e}");
                }
                catch (RlpException e)
                {
                    if (_logger.IsError) _logger.Error($"Couldn't deserialize state for address {address}.", e);
                }

                return value;
            }

            UInt256 GetCurrentNonce(IDictionary<Address, UInt256> noncesDictionary, Address address)
            {
                if (!noncesDictionary.TryGetValue(address, out var nonce))
                {
                    noncesDictionary[address] = nonce = GetFromState(_stateReader.GetNonce, address, UInt256.Zero);
                }
                
                return nonce;
            }

            UInt256 GetRemainingBalance(IDictionary<Address, UInt256> balances, Address address)
            {
                if (!balances.TryGetValue(address, out var balance))
                {
                    balances[address] = balance = GetFromState(_stateReader.GetBalance, address, UInt256.Zero);
                }

                return balance;
            }

            bool HasEnoughFounds(IDictionary<Address, UInt256> balances, Transaction transaction)
            {
                var balance = GetRemainingBalance(balances, transaction.SenderAddress);
                var transactionPotentialCost = transaction.GasPrice * (ulong) transaction.GasLimit + transaction.Value;

                if (balance < transactionPotentialCost)
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejecting transaction - transaction cost ({transactionPotentialCost}) is higher than sender balance ({balance}).");
                    return false;
                }

                balances[transaction.SenderAddress] = balance - transactionPotentialCost;
                return true;
            }

            var pendingTransactions = _transactionPool.GetPendingTransactions();
            var transactions = pendingTransactions.OrderBy(t => t.Nonce).ThenByDescending(t => t.GasPrice).ThenBy(t => t.GasLimit);
            IDictionary<Address, UInt256> remainingBalance = new Dictionary<Address, UInt256>();
            Dictionary<Address, UInt256> nonces = new Dictionary<Address, UInt256>();
            List<Transaction> selected = new List<Transaction>();
            long gasRemaining = gasLimit;

            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at min gas price {_minGasPriceForMining} and block gas limit {gasRemaining}.");

            foreach (Transaction tx in transactions)
            {
                if (gasRemaining < Transaction.BaseTxGasCost)
                {
                    continue;
                }

                if (tx.GasLimit > gasRemaining)
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (tx gas limit {tx.GasLimit} above remaining block gas {gasRemaining}) {tx.ToShortString()}");
                    continue;
                }
                
                if (tx.SenderAddress == null)
                {
                    _transactionPool.RemoveTransaction(tx.Hash, 0);
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (null sender) {tx.ToShortString()}");
                    continue;
                }

                if (tx.GasPrice < _minGasPriceForMining)
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (gas price too low - min gas price: {_minGasPriceForMining}) {tx.ToShortString()}");
                    continue;
                }

                UInt256 expectedNonce = GetCurrentNonce(nonces, tx.SenderAddress);
                if (expectedNonce != tx.Nonce)
                {
                    if (tx.Nonce < expectedNonce)
                    {
                        _transactionPool.RemoveTransaction(tx.Hash, 0);    
                    }
                    
                    if (tx.Nonce > expectedNonce + 16)
                    {
                        _transactionPool.RemoveTransaction(tx.Hash, 0);    
                    }
                    
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (invalid nonce - expected {expectedNonce}) {tx.ToShortString()}");
                    continue;
                }

                if (!HasEnoughFounds(remainingBalance, tx))
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (sender balance too low) {tx.ToShortString()}");
                    continue;
                }

                selected.Add(tx);
                nonces[tx.SenderAddress] = tx.Nonce + 1;
                gasRemaining -= tx.GasLimit;
            }

            if (_logger.IsDebug) _logger.Debug($"Collected {selected.Count} out of {pendingTransactions.Length} pending transactions.");

            return selected;
        }

        private readonly int _id = ITxSource.IdCounter;
        public override string ToString() => $"{GetType().Name}_{_id}";

    }
}
