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
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Core.Specs;
using Nethermind.Trie;

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
        public ITimestamper Timestamper { get; }
        public event EventHandler<BlockEventArgs>? BlockProduced;

        private ISealer Sealer { get; }
        private IStateProvider StateProvider { get; }
        private readonly IGasLimitCalculator _gasLimitCalculator;
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly ISpecProvider _specProvider;
        private readonly ITxSource _txSource;
        private readonly IBlockProductionTrigger _trigger;
        private bool _isRunning;
        private readonly ManualResetEvent _producingBlockLock = new(true);
        private CancellationTokenSource? _producerCancellationToken;

        private DateTime _lastProducedBlockDateTime;
        private const int BlockProductionTimeout = 1000;
        protected ILogger Logger { get; }

        protected BlockProducerBase(
            ITxSource? txSource,
            IBlockchainProcessor? processor,
            ISealer? sealer,
            IBlockTree? blockTree,
            IBlockProductionTrigger? trigger,
            IStateProvider? stateProvider,
            IGasLimitCalculator? gasLimitCalculator,
            ITimestamper? timestamper,
            ISpecProvider? specProvider,
            ILogManager? logManager,
            IDifficultyCalculator? difficultyCalculator)
        {
            _txSource = txSource ?? throw new ArgumentNullException(nameof(txSource));
            Processor = processor ?? throw new ArgumentNullException(nameof(processor));
            Sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
            BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            StateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _gasLimitCalculator = gasLimitCalculator ?? throw new ArgumentNullException(nameof(gasLimitCalculator));
            Timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
            _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));
            Logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private void OnTriggerBlockProduction(object? sender, BlockProductionEventArgs e)
        {
            e.BlockProductionTask = TryProduceAndAnnounceNewBlock(e.CancellationToken, e.ParentHeader, e.BlockTracer);
        }

        public virtual void Start()
        {
            _producerCancellationToken = new CancellationTokenSource();
            _isRunning = true;
            _trigger.TriggerBlockProduction += OnTriggerBlockProduction;
            _lastProducedBlockDateTime = DateTime.UtcNow;
        }

        public virtual Task StopAsync()
        {
            _producerCancellationToken?.Cancel();
            _isRunning = false;
            _trigger.TriggerBlockProduction -= OnTriggerBlockProduction;
            _producerCancellationToken?.Dispose();
            return Task.CompletedTask;
        }

        protected virtual bool IsRunning() => _isRunning;

        public bool IsProducingBlocks(ulong? maxProducingInterval)
        {
            if (Logger.IsTrace) Logger.Trace($"Checking IsProducingBlocks: maxProducingInterval {maxProducingInterval}, _lastProducedBlock {_lastProducedBlockDateTime}, IsRunning() {IsRunning()}");
            return IsRunning() && (maxProducingInterval == null || _lastProducedBlockDateTime.AddSeconds(maxProducingInterval.Value) > DateTime.UtcNow);
        }

        private async Task<Block?> TryProduceAndAnnounceNewBlock(CancellationToken token, BlockHeader? parentHeader = null, IBlockTracer? blockTracer = null)
        {
            using CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, _producerCancellationToken!.Token);
            token = tokenSource.Token;
            
            Block? block = null;
            if (await _producingBlockLock.WaitOneAsync(BlockProductionTimeout, token))
            {
                try
                {
                    block = await TryProduceNewBlock(token, parentHeader, blockTracer);
                    if (block is not null)
                    {
                        BlockProduced?.Invoke(this, new BlockEventArgs(block));
                    }
                }
                catch (Exception e) when (!(e is TaskCanceledException))
                {
                    if (Logger.IsError) Logger.Error("Failed to produce block", e);
                    Metrics.FailedBlockSeals++;
                    throw;
                }
                finally
                {
                    _producingBlockLock.Set();
                }
            }
            else
            {
                if (Logger.IsInfo) Logger.Info("Failed to produce block, previous block is still being produced");
            }

            return block;
        }

        private Task<Block?> TryProduceNewBlock(CancellationToken token, BlockHeader? parentHeader = null, IBlockTracer? blockTracer = null)
        {
            parentHeader = GetProducedBlockParent(parentHeader);
            if (parentHeader == null)
            {
                if (Logger.IsWarn) Logger.Warn("Preparing new block - parent header is null");
            }
            else
            {
                if (Sealer.CanSeal(parentHeader.Number + 1, parentHeader.Hash))
                {
                    Interlocked.Exchange(ref Metrics.CanProduceBlocks, 1);
                    return ProduceNewBlock(parentHeader, token, blockTracer);
                }
                else
                {
                    Interlocked.Exchange(ref Metrics.CanProduceBlocks, 0);
                }
            }

            Metrics.FailedBlockSeals++;
            return Task.FromResult((Block?)null);
        }

        protected virtual BlockHeader? GetProducedBlockParent(BlockHeader? parentHeader) => parentHeader ?? BlockTree.Head?.Header;

        private Task<Block?> ProduceNewBlock(BlockHeader parent, CancellationToken token, IBlockTracer? blockTracer)
        {
            if (TrySetState(parent.StateRoot))
            {
                Block block = PrepareBlock(parent);
                if (PreparedBlockCanBeMined(block))
                {
                    Block? processedBlock = ProcessPreparedBlock(block, blockTracer);
                    if (processedBlock is null)
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
            }

            return Task.FromResult((Block?)null);
        }

        private bool TrySetState(Keccak? parentStateRoot)
        {
            bool HasState(Keccak stateRoot)
            {
                RootCheckVisitor visitor = new();
                StateProvider.Accept(visitor, stateRoot);
                return visitor.HasRoot;
            }
            
            if (parentStateRoot is not null && HasState(parentStateRoot))
            {
                StateProvider.StateRoot = parentStateRoot;
                return true;
            }

            return false;
        }
        
        protected virtual Task<Block> SealBlock(Block block, BlockHeader parent, CancellationToken token) =>
            Sealer.SealBlock(block, token);

        protected virtual Block? ProcessPreparedBlock(Block block, IBlockTracer? blockTracer) =>
            Processor.Process(block, ProcessingOptions.ProducingBlock, blockTracer ?? NullBlockTracer.Instance);

        private bool PreparedBlockCanBeMined(Block? block)
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
            return _txSource.GetTransactions(parent, gasLimit);
        }

        protected virtual Block PrepareBlock(BlockHeader parent)
        {
            UInt256 timestamp = UInt256.Max(parent.Timestamp + 1, Timestamper.UnixTime.Seconds);
            BlockHeader header = new(
                parent.Hash!,
                Keccak.OfAnEmptySequenceRlp,
                Sealer.Address,
                UInt256.Zero, 
                parent.Number + 1,
                _gasLimitCalculator.GetGasLimit(parent),
                timestamp,
                GetExtraData(parent))
            {
                Author = Sealer.Address
            };
            
            UInt256 difficulty = _difficultyCalculator.Calculate(header, parent);
            header.Difficulty = difficulty;
            header.TotalDifficulty = parent.TotalDifficulty + difficulty;

            if (Logger.IsDebug) Logger.Debug($"Setting total difficulty to {parent.TotalDifficulty} + {difficulty}.");
            header.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, _specProvider.GetSpec(header.Number));

            IEnumerable<Transaction> transactions = GetTransactions(parent);
            return new BlockToProduce(header, transactions, Array.Empty<BlockHeader>());
        }

        private byte[] GetExtraData(BlockHeader parent) => Encoding.UTF8.GetBytes("Nethermind");
    }
}
