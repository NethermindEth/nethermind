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
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Blockchain
{
    [Todo("Introduce strategy for collecting Transactions for the block?")]
    public class DevBlockProducer : IBlockProducer
    {
        private static readonly BigInteger MinGasPriceForMining = 1;
        private readonly IBlockTree _blockTree;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;

        private readonly IBlockchainProcessor _processor;
        private readonly ITxPool _txPool;

        public DevBlockProducer(
            ITxPool txPool,
            IBlockchainProcessor devProcessor,
            IBlockTree blockTree,
            ITimestamper timestamper,
            ILogManager logManager)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _processor = devProcessor ?? throw new ArgumentNullException(nameof(devProcessor));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _timestamper = timestamper;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Start()
        {
            _txPool.NewPending += OnNewPendingTx;
        }

        public async Task StopAsync()
        {
            _txPool.NewPending -= OnNewPendingTx;
            await Task.CompletedTask;
        }

        private Block PrepareBlock()
        {
            BlockHeader parentHeader = _blockTree.Head;
            if (parentHeader == null) return null;

            Block parent = _blockTree.FindBlock(parentHeader.Hash, BlockTreeLookupOptions.None);
            UInt256 timestamp = _timestamper.EpochSeconds;

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

            var transactions = _txPool.GetPendingTransactions().OrderBy(t => t?.Nonce); // by nonce in case there are two transactions for the same account

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
            header.TxRoot = block.CalculateTxRoot();
            return block;
        }

        public void ProduceEmptyBlock()
        {
            ProduceNewBlock();
        }
        
        private void OnNewPendingTx(object sender, TxEventArgs e)
        {
            ProduceNewBlock();
        }

        private void ProduceNewBlock()
        {
            Block block = PrepareBlock();
            if (block == null)
            {
                if (_logger.IsError) _logger.Error("Failed to prepare block for mining.");
                return;
            }

            Block processedBlock = _processor.Process(block, ProcessingOptions.NoValidation | ProcessingOptions.ReadOnlyChain | ProcessingOptions.WithRollback, NullBlockTracer.Instance);
            if (_logger.IsInfo) _logger.Info($"Mined a DEV block {processedBlock.ToString(Block.Format.FullHashAndNumber)} State Root: {processedBlock.StateRoot}");
            
            if (processedBlock == null)
            {
                if (_logger.IsError) _logger.Error("Block prepared by block producer was rejected by processor");
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Mined a DEV block {processedBlock.ToString(Block.Format.FullHashAndNumber)}");
            _blockTree.SuggestBlock(processedBlock);
        }
    }
}