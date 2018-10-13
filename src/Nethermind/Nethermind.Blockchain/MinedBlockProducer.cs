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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;

namespace Nethermind.Blockchain
{
    public class MinedBlockProducer : IBlockProducer
    {
        private static readonly BigInteger MinGasPriceForMining = 1;

        private readonly IBlockchainProcessor _processor;
        private readonly ISealEngine _sealEngine;
        private readonly IBlockTree _blockTree;
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly ITransactionStore _transactionStore;
        private readonly ILogger _logger;

        public MinedBlockProducer(
            IDifficultyCalculator difficultyCalculator,
            ITransactionStore transactionStore,
            IBlockchainProcessor processor,
            ISealEngine sealEngine,
            IBlockTree blockTree,
            ILogManager logManager)
        {
            _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _sealEngine = sealEngine ?? throw new ArgumentNullException(nameof(sealEngine));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private object _syncToken = new object();
        
        private void BlockTreeOnNewBestSuggestedBlock(object sender, BlockEventArgs e)
        {
            lock (_syncToken)
            {
                _cancellationTokenSource?.Cancel();
            }
        }

        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private void OnBlockProcessorQueueEmpty(object sender, EventArgs e)
        {
            CancellationToken token;
            lock (_syncToken)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                token = _cancellationTokenSource.Token;
            }

            if (!_sealEngine.IsMining)
            {
                return;
            }
            
            Block block = PrepareBlock();
            if (block == null)
            {
                if(_logger.IsError) _logger.Error("Failed to prepare block for mining.");
                return;
            }

            Block processedBlock = _processor.Process(block, ProcessingOptions.ReadOnlyChain, NullTraceListener.Instance);
            _sealEngine.MineAsync(processedBlock, token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    _blockTree.SuggestBlock(t.Result);
                }
                else if(t.IsFaulted)
                {
                    _logger.Error("Mining failer", t.Exception);
                }
                else if(t.IsCanceled)
                {
                    if(_logger.IsDebug) _logger.Debug($"Mining block {processedBlock.ToString(Block.Format.HashAndNumber)} cancelled");
                }
            }, token);
        }

        private Block PrepareBlock()
        {
            BlockHeader parentHeader = _blockTree.Head;
            if (parentHeader == null)
            {
                return null;
            }

            Block parent = _blockTree.FindBlock(parentHeader.Hash, false);
            UInt256 timestamp = Timestamp.UnixUtcUntilNowSecs;

            UInt256 difficulty = _difficultyCalculator.Calculate(parent.Difficulty, parent.Timestamp, Timestamp.UnixUtcUntilNowSecs, parent.Number + 1, parent.Ommers.Length > 0);
            BlockHeader header = new BlockHeader(
                parent.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                difficulty,
                parent.Number + 1,
                parent.GasLimit,
                timestamp > parent.Timestamp ? timestamp : parent.Timestamp + 1,
                Encoding.UTF8.GetBytes("Nethermind"));

            header.TotalDifficulty = parent.TotalDifficulty + difficulty;
            if (_logger.IsDebug) _logger.Debug($"Setting total difficulty to {parent.TotalDifficulty} + {difficulty}.");

            var transactions = _transactionStore.GetAllPending().OrderBy(t => t?.Nonce); // by nonce in case there are two transactions for the same account, TODO: test it

            List<Transaction> selected = new List<Transaction>();
            BigInteger gasRemaining = header.GasLimit;

            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at min gas price {MinGasPriceForMining} and block gas limit {gasRemaining}.");

            int total = 0;
            foreach (Transaction transaction in transactions)
            {
                total++;
                if (transaction == null)
                {
                    throw new InvalidOperationException("Block transaction is null");
                }

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

                selected.Add(transaction);
                gasRemaining -= transaction.GasLimit;
            }

            if (_logger.IsDebug) _logger.Debug($"Collected {selected.Count} out of {total} pending transactions.");
            Block block = new Block(header, selected, new BlockHeader[0]);
            header.TransactionsRoot = block.CalculateTransactionsRoot();
            return block;
        }

        public void Start()
        {
            _processor.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
            _blockTree.NewBestSuggestedBlock += BlockTreeOnNewBestSuggestedBlock;
        }

        public async Task StopAsync()
        {
            _processor.ProcessingQueueEmpty -= OnBlockProcessorQueueEmpty;
            _blockTree.NewBestSuggestedBlock -= BlockTreeOnNewBestSuggestedBlock;
            lock (_syncToken)
            {
                _cancellationTokenSource?.Cancel();
            }
            
            await Task.CompletedTask;
        }
    }
}