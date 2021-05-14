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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Producers
{
    /// <summary>
    /// I think this class can be significantly simplified if we split the block production into a pipeline:
    /// * signal block needed
    /// * prepare block frame
    /// * select transactions
    /// * seal
    /// Then the pipeline can be build from separate components
    /// And each separate component can be tested independently
    /// This would also simplify injection of various behaviours into NethDev pipeline
    /// </summary>
    public abstract class BlockProducerBase : IBlockProducer
    {
        private IBlockchainProcessor Processor { get; }
        protected IBlockTree BlockTree { get; }
        protected IBlockProcessingQueue BlockProcessingQueue { get; }
        public ITimestamper Timestamper { get; }

        protected ISealer Sealer { get; }
        protected IStateProvider StateProvider { get; }
        private readonly IGasLimitCalculator _gasLimitCalculator;
        private readonly ITimestamper _timestamper;
        private readonly ISpecProvider _spec;
        private readonly ITxSource _txSource;

        protected DateTime _lastProducedBlockDateTime;
        protected ILogger Logger { get; }

        protected BlockProducerBase(
            ITxSource? txSource,
            IBlockchainProcessor? processor,
            ISealer? sealer,
            IBlockTree? blockTree,
            IBlockProcessingQueue? blockProcessingQueue,
            IStateProvider? stateProvider,
            IGasLimitCalculator? gasLimitCalculator,
            ITimestamper? timestamper,
            ISpecProvider? specProvider,
            ILogManager? logManager)
        {
            _txSource = txSource ?? throw new ArgumentNullException(nameof(txSource));
            Processor = processor ?? throw new ArgumentNullException(nameof(processor));
            Sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
            BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            BlockProcessingQueue = blockProcessingQueue ?? throw new ArgumentNullException(nameof(blockProcessingQueue));
            StateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _gasLimitCalculator = gasLimitCalculator ?? throw new ArgumentNullException(nameof(gasLimitCalculator));
            Timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _spec = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            Logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public abstract void Start();

        public abstract Task StopAsync();

        protected abstract bool IsRunning();

        public bool IsProducingBlocks(ulong? maxProducingInterval)
        {
            if (Logger.IsTrace)
                Logger.Trace(
                    $"Checking IsProducingBlocks: maxProducingInterval {maxProducingInterval}, _lastProducedBlock {_lastProducedBlockDateTime}, IsRunning() {IsRunning()}");
            if (IsRunning() == false)
                return false;
            if (maxProducingInterval != null)
                return _lastProducedBlockDateTime.AddSeconds(maxProducingInterval.Value) > DateTime.UtcNow;
            else
                return true;
        }
        
        private readonly object _newBlockLock = new();

        protected Task<Block?> TryProduceNewBlock(CancellationToken token, BlockHeader? parentHeader = null)
        {
            lock (_newBlockLock)
            {
                parentHeader ??= BlockTree.Head?.Header;
                if (parentHeader == null)
                {
                    if (Logger.IsWarn) Logger.Warn("Preparing new block - parent header is null");
                }
                else
                {
                    if (Sealer.CanSeal(parentHeader.Number + 1, parentHeader.Hash))
                    {
                        Interlocked.Exchange(ref Metrics.CanProduceBlocks, 1);
                        return ProduceNewBlock(parentHeader, token);
                    }
                    else
                    {
                        Interlocked.Exchange(ref Metrics.CanProduceBlocks, 0);
                    }
                }

                Metrics.FailedBlockSeals++;
                return Task.FromResult((Block?)null);
            }
        }

        private Task<Block?> ProduceNewBlock(BlockHeader parent, CancellationToken token)
        {
            StateProvider.StateRoot = parent.StateRoot;
            Block block = PrepareBlock(parent);
            if (PreparedBlockCanBeMined(block))
            {
                Block? processedBlock = ProcessPreparedBlock(block);
                if (processedBlock == null)
                {
                    if (Logger.IsError) Logger.Error("Block prepared by block producer was rejected by processor.");
                    Metrics.FailedBlockSeals++;
                }
                else
                {
                    return SealBlock(processedBlock, parent, token).ContinueWith((Func<Task<Block?>, Block?>)(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            if (t.Result != null)
                            {
                                if (Logger.IsInfo)
                                    Logger.Info($"Sealed block {t.Result.ToString(Block.Format.HashNumberDiffAndTx)}");
                                ConsumeProducedBlock(t.Result);
                                Metrics.BlocksSealed++;
                                _lastProducedBlockDateTime = DateTime.UtcNow;
                                return t.Result;
                            }
                            else
                            {
                                if (Logger.IsInfo)
                                    Logger.Info(
                                        $"Failed to seal block {processedBlock.ToString(Block.Format.HashNumberDiffAndTx)} (null seal)");
                                Metrics.FailedBlockSeals++;
                            }
                        }
                        else if (t.IsFaulted)
                        {
                            if (Logger.IsError) Logger.Error("Mining failed", t.Exception);
                            Metrics.FailedBlockSeals++;
                        }
                        else if (t.IsCanceled)
                        {
                            if (Logger.IsInfo) Logger.Info($"Sealing block {processedBlock.Number} cancelled");
                            Metrics.FailedBlockSeals++;
                        }

                        return null;
                    }), token);
                }
            }

            return Task.FromResult((Block?)null);
        }

        protected virtual void ConsumeProducedBlock(Block block) => BlockTree.SuggestBlock(block);

        protected virtual Task<Block> SealBlock(Block block, BlockHeader parent, CancellationToken token) =>
            Sealer.SealBlock(block, token);

        protected virtual Block? ProcessPreparedBlock(Block block) =>
            Processor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);

        protected virtual bool PreparedBlockCanBeMined(Block? block)
        {
            if (block == null)
            {
                if (Logger.IsError) Logger.Error("Failed to prepare block for mining.");
                return false;
            }

            return true;
        }
        
        private IEnumerable<Transaction> GetTransactions(BlockHeader parent)
        {
            long gasLimit = _gasLimitCalculator.GetGasLimit(parent);
            bool isEip1559Enabled = _spec.GetSpec(parent.Number + 1).IsEip1559Enabled;
            long gasLimitEip1559Adjusted = Eip1559GasLimitAdjuster.AdjustGasLimit(isEip1559Enabled, gasLimit);
            return _txSource.GetTransactions(parent, gasLimitEip1559Adjusted);
        }

        protected virtual Block PrepareBlock(BlockHeader parent)
        {
            UInt256 timestamp = UInt256.Max(parent.Timestamp + 1, Timestamper.UnixTime.Seconds);
            UInt256 difficulty = CalculateDifficulty(parent, timestamp);
            BlockHeader header = new(
                parent.Hash!,
                Keccak.OfAnEmptySequenceRlp,
                Sealer.Address,
                difficulty,
                parent.Number + 1,
                _gasLimitCalculator.GetGasLimit(parent),
                timestamp,
                Encoding.UTF8.GetBytes("Nethermind"))
            {
                TotalDifficulty = parent.TotalDifficulty + difficulty, Author = Sealer.Address
            };

            if (Logger.IsDebug) Logger.Debug($"Setting total difficulty to {parent.TotalDifficulty} + {difficulty}.");
            header.BaseFee = BlockHeader.CalculateBaseFee(parent, _spec.GetSpec(header.Number));

            IEnumerable<Transaction> transactions = GetTransactions(parent);
            return new BlockToProduce(header, transactions, Array.Empty<BlockHeader>());;
        }

        protected abstract UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp);
    }
}
