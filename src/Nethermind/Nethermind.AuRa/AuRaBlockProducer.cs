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
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.AuRa.Config;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Store;

namespace Nethermind.AuRa
{
    public class AuRaBlockProducer : IBlockProducer
    {
        private static readonly BigInteger MinGasPriceForMining = 1;
        
        private readonly IBlockTree _blockTree;
        private readonly ITimestamper _timestamper;
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        private readonly Address _nodeAddress;
        private readonly ISealer _sealer;
        private readonly IStateProvider _stateProvider;
        private readonly IAuraConfig _config;
        private readonly ILogger _logger;

        private readonly IBlockchainProcessor _processor;
        private readonly ITxPool _txPool;
        private Task _producerTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        
        public AuRaBlockProducer(ITxPool txPool,
            IBlockchainProcessor blockchainProcessor,
            IBlockTree blockTree,
            ITimestamper timestamper,
            IAuRaStepCalculator auRaStepCalculator,
            Address nodeAddress,
            ISealer sealer,
            IStateProvider stateProvider,
            IAuraConfig config,
            ILogManager logManager)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _processor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _auRaStepCalculator = auRaStepCalculator ?? throw new ArgumentNullException(nameof(auRaStepCalculator));
            _nodeAddress = nodeAddress ?? throw new ArgumentNullException(nameof(nodeAddress));
            _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
            _stateProvider = stateProvider  ?? throw new ArgumentNullException(nameof(stateProvider));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Start()
        {
            _producerTask = Task.Run(ProducerLoop, _cancellationTokenSource.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("AuRa block producer encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsDebug) _logger.Debug("AuRa block producer stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsDebug) _logger.Debug("AuRa block producer complete.");
                }
            });
        }

        private async Task ProducerLoop()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                BlockHeader parentHeader = _blockTree.Head;
                if (parentHeader == null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Preparing new block - parent header is null");
                }
                else if (_sealer.CanSeal(parentHeader.Number + 1, parentHeader.Hash))
                {
                    ProduceNewBlock(parentHeader);
                }

                var timeToNextStep = _auRaStepCalculator.TimeToNextStep;
                if (_logger.IsDebug) _logger.Debug($"Waiting {timeToNextStep} for next AuRa step.");
                await TaskExt.DelayAtLeast(timeToNextStep);
            }
        }

        public async Task StopAsync()
        {
            _cancellationTokenSource?.Cancel();
            await (_producerTask ?? Task.CompletedTask);
        }

        private Block PrepareBlock(BlockHeader parentHeader)
        {
            UInt256 timestamp = _timestamper.EpochSeconds;

            BlockHeader header = new BlockHeader(
                parentHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                _nodeAddress,
                AuraDifficultyCalculator.CalculateDifficulty(parentHeader.AuRaStep.Value, _auRaStepCalculator.CurrentStep, 0),
                parentHeader.Number + 1,
                parentHeader.GasLimit,
                timestamp > parentHeader.Timestamp ? timestamp : parentHeader.Timestamp + 1,
                Encoding.UTF8.GetBytes("Nethermind"))
            {
                AuRaStep = (long) _auRaStepCalculator.CurrentStep,
            };

            header.TotalDifficulty = parentHeader.TotalDifficulty + header.Difficulty;
            if (_logger.IsDebug) _logger.Debug($"Setting total difficulty to {parentHeader.TotalDifficulty} + {header.Difficulty}.");

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

        private void ProduceNewBlock(BlockHeader parentHeader)
        {
            _stateProvider.StateRoot = parentHeader.StateRoot;
            
            Block block = PrepareBlock(parentHeader);
            if (block == null)
            {
                if (_logger.IsError) _logger.Error("Failed to prepare block for mining.");
                return;
            }
            
            if (block.Transactions.Length == 0)
            {
                if (_config.ForceSealing)
                {
                    if (_logger.IsDebug) _logger.Debug($"Force sealing block {block.Number} without transactions.");                    
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug($"Skip seal block {block.Number}, no transactions pending.");
                    return;
                }
            }

            Block processedBlock = _processor.Process(block, ProcessingOptions.NoValidation | ProcessingOptions.ReadOnlyChain | ProcessingOptions.WithRollback, NullBlockTracer.Instance);
            if (_logger.IsInfo) _logger.Info($"Mined a DEV block {processedBlock.ToString(Block.Format.FullHashAndNumber)} State Root: {processedBlock.StateRoot}");
            
            if (processedBlock == null)
            {
                if (_logger.IsError) _logger.Error("Block prepared by block producer was rejected by processor");
                return;
            }
            
            _sealer.SealBlock(processedBlock, _cancellationTokenSource.Token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    if (t.Result != null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Sealed block {t.Result.ToString(Block.Format.HashNumberDiffAndTx)}");
                        _blockTree.SuggestBlock(t.Result);
                    }
                    else
                    {
                        if (_logger.IsInfo) _logger.Info($"Failed to seal block {processedBlock.ToString(Block.Format.HashNumberDiffAndTx)} (null seal)");
                    }
                }
                else if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Mining failed", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsInfo) _logger.Info($"Sealing block {processedBlock.Number} cancelled");
                }
            }, _cancellationTokenSource.Token);
        }
    }
}