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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Comparers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.TxPool;

[assembly:InternalsVisibleTo("Nethermind.AuRa.Test")]

namespace Nethermind.Blockchain.Producers
{
    public class TxPoolTxSource : ITxSource
    {
        private readonly ITxPool _transactionPool;
        private readonly IStateReader _stateReader;
        private readonly ITransactionComparerProvider _transactionComparerProvider;
        private readonly ITxFilterPipeline _txFilterPipeline;
        private readonly ISpecProvider _specProvider;
        protected readonly ILogger _logger;

        public TxPoolTxSource(ITxPool? transactionPool, IStateReader? stateReader, ISpecProvider? specProvider, ITransactionComparerProvider transactionComparerProvider, ILogManager? logManager, ITxFilterPipeline? txFilterPipeline)
        {
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _transactionComparerProvider = transactionComparerProvider ?? throw new ArgumentNullException(nameof(transactionComparerProvider));
            _txFilterPipeline = txFilterPipeline ?? throw new ArgumentNullException(nameof(txFilterPipeline));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger<TxPoolTxSource>() ?? throw new ArgumentNullException(nameof(logManager));
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
                if (!noncesDictionary.TryGetValue(address, out UInt256 nonce))
                {
                    noncesDictionary[address] = nonce = GetFromState(_stateReader.GetNonce, address, UInt256.Zero);
                }
                
                return nonce;
            }

            UInt256 GetRemainingBalance(IDictionary<Address, UInt256> balances, Address address)
            {
                if (!balances.TryGetValue(address, out UInt256 balance))
                {
                    balances[address] = balance = GetFromState(_stateReader.GetBalance, address, UInt256.Zero);
                }

                return balance;
            }

            bool HasEnoughFounds(IDictionary<Address, UInt256> balances, Transaction transaction, bool isEip1559Enabled, UInt256 baseFee)
            {
                UInt256 balance = GetRemainingBalance(balances, transaction.SenderAddress!);
                UInt256 transactionPotentialCost = transaction.CalculateTransactionPotentialCost(isEip1559Enabled, baseFee);

                if (balance < transactionPotentialCost)
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejecting transaction - transaction cost ({transactionPotentialCost}) is higher than sender balance ({balance}).");
                    return false;
                }

                balances[transaction.SenderAddress] = balance - transactionPotentialCost;
                return true;
            }

            long blockNumber = parent.Number + 1;
            IReleaseSpec releaseSpec = _specProvider.GetSpec(blockNumber);
            bool isEip1559Enabled = releaseSpec.IsEip1559Enabled;
            UInt256 baseFee = BlockHeader.CalculateBaseFee(parent, releaseSpec);
            IDictionary<Address, WrappedTransaction[]> pendingTransactions = _transactionPool.GetPendingTransactionsBySender();
            IComparer<WrappedTransaction> comparer = GetComparer(parent, new BlockPreparationContext(baseFee, blockNumber))
                .ThenBy(DistinctCompareTx.Instance); // in order to sort properly and not loose transactions we need to differentiate on their identity which provided comparer might not be doing
            
            IEnumerable<WrappedTransaction> transactions = GetOrderedTransactions(pendingTransactions, comparer);
            IDictionary<Address, UInt256> remainingBalance = new Dictionary<Address, UInt256>();
            Dictionary<Address, UInt256> nonces = new();
            List<Transaction> selected = new();
            long gasRemaining = gasLimit;

            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at block gas limit {gasRemaining}.");


            foreach (WrappedTransaction wTx in transactions)
            {
                if (gasRemaining < Transaction.BaseTxGasCost)
                {
                    break;
                }

                if (wTx.Tx.GasLimit > gasRemaining)
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (tx gas limit {wTx.Tx.GasLimit} above remaining block gas {gasRemaining}) {wTx.Tx.ToShortString()}");
                    continue;
                }
                
                if (wTx.Tx.SenderAddress == null)
                {
                    _transactionPool.RemoveTransaction(wTx.Tx);
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (null sender) {wTx.Tx.ToShortString()}");
                    continue;
                }

                bool success = _txFilterPipeline.Execute(wTx.Tx, parent);
                if (!success)
                {
                    _transactionPool.RemoveTransaction(wTx.Tx);
                    continue;
                }

                UInt256 expectedNonce = GetCurrentNonce(nonces, wTx.Tx.SenderAddress);
                if (expectedNonce != wTx.Tx.Nonce)
                {
                    if (wTx.Tx.Nonce < expectedNonce)
                    {
                        _transactionPool.RemoveTransaction(wTx.Tx, true);    
                    }
                    
                    if (wTx.Tx.Nonce > expectedNonce + _transactionPool.FutureNonceRetention)
                    {
                        _transactionPool.RemoveTransaction(wTx.Tx);
                    }
                    
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (invalid nonce - expected {expectedNonce}) {wTx.Tx.ToShortString()}");
                    continue;
                }
                
                if (!HasEnoughFounds(remainingBalance, wTx.Tx, isEip1559Enabled, baseFee))
                {
                    _transactionPool.RemoveTransaction(wTx.Tx);
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (sender balance too low) {wTx.Tx.ToShortString()}");
                    continue;
                }
                

                selected.Add(wTx.Tx);
                if (_logger.IsTrace) _logger.Trace($"Selected {wTx.Tx.ToShortString()} to be included in block.");
                nonces[wTx.Tx.SenderAddress!] = wTx.Tx.Nonce + 1;
                gasRemaining -= wTx.Tx.GasLimit;
            }

            if (_logger.IsDebug) _logger.Debug($"Collected {selected.Count} out of {pendingTransactions.Sum(g => g.Value.Length)} pending transactions.");

            return selected;
        }
        
        protected virtual IEnumerable<WrappedTransaction> GetOrderedTransactions(IDictionary<Address,WrappedTransaction[]> pendingTransactions, IComparer<WrappedTransaction> comparer) => 
            Order(pendingTransactions, comparer);

        protected virtual IComparer<WrappedTransaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext) 
            => _transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        internal static IEnumerable<WrappedTransaction> Order(IDictionary<Address,WrappedTransaction[]> pendingTransactions, IComparer<WrappedTransaction> comparerWithIdentity)
        {
            IEnumerator<WrappedTransaction>[] bySenderEnumerators = pendingTransactions
                .Select<KeyValuePair<Address, WrappedTransaction[]>, IEnumerable<WrappedTransaction>>(g => g.Value)
                .Select(g => g.GetEnumerator())
                .ToArray();
            
            try
            {
                // we create a sorted list of head of each group of transactions. From:
                // A -> N0_P3, N1_P1, N1_P0, N3_P5...
                // B -> N4_P4, N5_P3, N6_P3...
                // We construct [N4_P4 (B), N0_P3 (A)] in sorted order by priority
                DictionarySortedSet<WrappedTransaction, IEnumerator<WrappedTransaction>> transactions = new(comparerWithIdentity);
            
                for (int i = 0; i < bySenderEnumerators.Length; i++)
                {
                    IEnumerator<WrappedTransaction> enumerator = bySenderEnumerators[i];
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }
                }

                // while there are still unreturned transactions
                while (transactions.Count > 0)
                {
                    // we take first transaction from sorting order, on first call: N4_P4 from B
                    (WrappedTransaction tx, IEnumerator<WrappedTransaction> enumerator) = transactions.Min;

                    // we replace it by next transaction from same sender, on first call N5_P3 from B
                    transactions.Remove(tx);
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }

                    // we return transactions in lazy manner, no need to sort more than will be taken into block
                    yield return tx;
                }
            }
            finally
            {
                // disposing enumerators
                for (int i = 0; i < bySenderEnumerators.Length; i++)
                {
                    bySenderEnumerators[i].Dispose();
                }
            }
        }

        public override string ToString() => $"{nameof(TxPoolTxSource)}";
    }
}
