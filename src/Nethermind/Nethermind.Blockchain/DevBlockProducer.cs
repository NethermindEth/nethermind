/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;

namespace Nethermind.Blockchain
{
    public class DevBlockProducer : IBlockProducer
    {
        private static readonly BigInteger MinGasPriceForMining = 1;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        private readonly IBlockchainProcessor _processor;
        private readonly ITransactionStore _transactionStore;

        public DevBlockProducer(
            ITransactionStore transactionStore,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            ILogManager logManager)
        {
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Start()
        {
            _transactionStore.NewPending += OnNewPendingTx;
        }

        public async Task StopAsync()
        {
            _transactionStore.NewPending -= OnNewPendingTx;
            await Task.CompletedTask;
        }

        private Block PrepareBlock()
        {
            BlockHeader parentHeader = _blockTree.Head;
            if (parentHeader == null) return null;

            Block parent = _blockTree.FindBlock(parentHeader.Hash, false);
            UInt256 timestamp = Timestamp.UnixUtcUntilNowSecs;

            BlockHeader header = new BlockHeader(
                parent.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                1,
                parent.Number + 1,
                parent.GasLimit,
                timestamp > parent.Timestamp ? timestamp : parent.Timestamp + 1,
                Encoding.UTF8.GetBytes("Nethermind"));

            header.TotalDifficulty = parent.TotalDifficulty + header.Difficulty;
            if (_logger.IsDebug) _logger.Debug($"Setting total difficulty to {parent.TotalDifficulty} + {header.Difficulty}.");

            var transactions = _transactionStore.GetAllPending().OrderBy(t => t?.Nonce); // by nonce in case there are two transactions for the same account

            var selectedTxs = new List<Transaction>();
            BigInteger gasRemaining = header.GasLimit;

            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at min gas price {MinGasPriceForMining} and block gas limit {gasRemaining}.");

            int total = 0;
            foreach (Transaction transaction in transactions)
            {
                total++;
                if (transaction == null) throw new InvalidOperationException("Block transaction is null");

                if (transaction.GasPrice < MinGasPriceForMining)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - gas price ({transaction.GasPrice}) too low (min gas price: {MinGasPriceForMining}.");
                    continue;
                }

                if (transaction.GasLimit > gasRemaining)
                {
                    if (_logger.IsTrace) _logger.Trace($"Rejecting transaction - gas limit ({transaction.GasPrice}) more than remaining gas ({gasRemaining}).");
                    break;
                }

                selectedTxs.Add(transaction);
                gasRemaining -= transaction.GasLimit;
            }

            if (_logger.IsDebug) _logger.Debug($"Collected {selectedTxs.Count} out of {total} pending transactions.");


            Block block = new Block(header, selectedTxs, new BlockHeader[0]);
            header.TransactionsRoot = block.CalculateTransactionsRoot();
            return block;
        }

        private void OnNewPendingTx(object sender, TransactionEventArgs e)
        {
            Block block = PrepareBlock();
            if (block == null)
            {
                if (_logger.IsError) _logger.Error("Failed to prepare block for mining.");
                return;
            }

            Block processedBlock = _processor.Process(block, ProcessingOptions.ReadOnlyChain | ProcessingOptions.StoreReceipts, NullTraceListener.Instance);
            if (processedBlock == null)
            {
                if(_logger.IsError) _logger.Error("Block prepared by block producer was rejected by processor");
                return;
            }
            
            _blockTree.SuggestBlock(processedBlock);
        }
    }
}