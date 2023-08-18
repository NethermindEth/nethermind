// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Producers
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
        private ITimestamper Timestamper { get; }
        public event EventHandler<BlockEventArgs>? BlockProduced;

        private ISealer Sealer { get; }
        private IWorldState StateProvider { get; }
        private readonly IGasLimitCalculator _gasLimitCalculator;
        private readonly IDifficultyCalculator _difficultyCalculator;
        protected readonly ISpecProvider _specProvider;
        private readonly ITxSource _txSource;
        private readonly IBlockProductionTrigger _trigger;
        private bool _isRunning;
        protected readonly SemaphoreSlim _producingBlockLock = new(1);
        private CancellationTokenSource? _producerCancellationToken;

        private DateTime _lastProducedBlockDateTime;
        protected const int BlockProductionTimeout = 2000;
        protected ILogger Logger { get; }
        protected readonly IBlocksConfig _blocksConfig;

        protected BlockProducerBase(
            ITxSource? txSource,
            IBlockchainProcessor? processor,
            ISealer? sealer,
            IBlockTree? blockTree,
            IBlockProductionTrigger? trigger,
            IWorldState? stateProvider,
            IGasLimitCalculator? gasLimitCalculator,
            ITimestamper? timestamper,
            ISpecProvider? specProvider,
            ILogManager? logManager,
            IDifficultyCalculator? difficultyCalculator,
            IBlocksConfig? blocksConfig)
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
            _blocksConfig = blocksConfig ?? throw new ArgumentNullException(nameof(blocksConfig));
        }

        private void OnTriggerBlockProduction(object? sender, BlockProductionEventArgs e)
        {
            BlockHeader? parent = BlockTree.GetProducedBlockParent(e.ParentHeader);
            e.BlockProductionTask = TryProduceAndAnnounceNewBlock(e.CancellationToken, parent, e.BlockTracer, e.PayloadAttributes);
        }

        public virtual Task Start()
        {
            _producerCancellationToken = new CancellationTokenSource();
            _isRunning = true;
            _trigger.TriggerBlockProduction += OnTriggerBlockProduction;
            _lastProducedBlockDateTime = DateTime.UtcNow;
            return Task.CompletedTask;
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
            return IsRunning() && (maxProducingInterval is null || _lastProducedBlockDateTime.AddSeconds(maxProducingInterval.Value) > DateTime.UtcNow);
        }

        private async Task<Block?> TryProduceAndAnnounceNewBlock(CancellationToken token, BlockHeader? parentHeader, IBlockTracer? blockTracer = null, PayloadAttributes? payloadAttributes = null)
        {
            using CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, _producerCancellationToken!.Token);
            token = tokenSource.Token;

            Block? block = null;
            if (await _producingBlockLock.WaitAsync(BlockProductionTimeout, token))
            {
                try
                {
                    block = await TryProduceNewBlock(token, parentHeader, blockTracer, payloadAttributes);
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
                    _producingBlockLock.Release();
                }
            }
            else
            {
                if (Logger.IsInfo) Logger.Info("Failed to produce block, previous block is still being produced");
            }

            return block;
        }

        protected virtual Task<Block?> TryProduceNewBlock(CancellationToken token, BlockHeader? parentHeader, IBlockTracer? blockTracer = null, PayloadAttributes? payloadAttributes = null)
        {
            if (parentHeader is null)
            {
                if (Logger.IsWarn) Logger.Warn("Preparing new block - parent header is null");
            }
            else
            {
                if (Sealer.CanSeal(parentHeader.Number + 1, parentHeader.Hash))
                {
                    Interlocked.Exchange(ref Metrics.CanProduceBlocks, 1);
                    return ProduceNewBlock(parentHeader, token, blockTracer, payloadAttributes);
                }
                else
                {
                    Interlocked.Exchange(ref Metrics.CanProduceBlocks, 0);
                }
            }

            Metrics.FailedBlockSeals++;
            return Task.FromResult((Block?)null);
        }

        private Task<Block?> ProduceNewBlock(BlockHeader parent, CancellationToken token, IBlockTracer? blockTracer, PayloadAttributes? payloadAttributes = null)
        {
            if (TrySetState(parent.StateRoot))
            {
                Block block = PrepareBlock(parent, payloadAttributes);
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
                                if (t.Result is not null)
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

        /// <summary>
        /// Sets the state to produce block on
        /// </summary>
        /// <param name="parentStateRoot">Parent block state</param>
        /// <returns>True if succeeded, false otherwise</returns>
        /// <remarks>Should be called inside <see cref="_producingBlockLock"/> lock.</remarks>
        protected bool TrySetState(Keccak? parentStateRoot)
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
            if (block is null)
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

        protected virtual BlockHeader PrepareBlockHeader(BlockHeader parent,
            PayloadAttributes? payloadAttributes = null)
        {
            ulong timestamp = payloadAttributes?.Timestamp ?? Math.Max(parent.Timestamp + 1, Timestamper.UnixTime.Seconds);
            Address blockAuthor = payloadAttributes?.SuggestedFeeRecipient ?? Sealer.Address;
            BlockHeader header = new(
                parent.Hash!,
                Keccak.OfAnEmptySequenceRlp,
                blockAuthor,
                UInt256.Zero,
                parent.Number + 1,
                payloadAttributes?.GasLimit ?? _gasLimitCalculator.GetGasLimit(parent),
                timestamp,
                _blocksConfig.GetExtraDataBytes())
            {
                Author = blockAuthor,
                MixHash = payloadAttributes?.PrevRandao,
                ParentBeaconBlockRoot = payloadAttributes?.ParentBeaconBlockRoot
            };

            UInt256 difficulty = _difficultyCalculator.Calculate(header, parent);
            header.Difficulty = difficulty;
            header.TotalDifficulty = parent.TotalDifficulty + difficulty;

            if (Logger.IsDebug) Logger.Debug($"Setting total difficulty to {parent.TotalDifficulty} + {difficulty}.");

            header.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, _specProvider.GetSpec(header));

            return header;
        }

        protected virtual Block PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader header = PrepareBlockHeader(parent, payloadAttributes);

            IEnumerable<Transaction> transactions = GetTransactions(parent);

            return new BlockToProduce(header, transactions, Array.Empty<BlockHeader>(), payloadAttributes?.Withdrawals);
        }
    }
}
