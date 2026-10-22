// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Ethash;

internal sealed class NethDevBlockProducerFactory(
    IBlockProducerEnvFactory blockProducerEnvFactory,
    IBlockTree blockTree,
    ITimestamper timestamper,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig,
    ITxPool txPool,
    IManualBlockProductionTrigger manualBlockProductionTrigger,
    ILogManager logManager)
    : IBlockProducerFactory, IBlockProducerRunnerFactory
{
    public IBlockProducer InitBlockProducer()
    {
        ILogger logger = logManager.GetClassLogger<NethDevBlockProducerFactory>();
        if (logger.IsInfo) logger.Info("Starting Neth Dev block producer & sealer");

        IBlockProducerEnv env = blockProducerEnvFactory.CreatePersistent();
        return new DevBlockProducer(
            env.TxSource,
            env.ChainProcessor,
            env.ReadOnlyStateProvider,
            blockTree,
            timestamper,
            specProvider,
            blocksConfig,
            logManager);
    }

    /// <remarks>
    /// The pool can hold transactions the producer never includes (fee below the base fee, nonce gap),
    /// so blocks triggered by a non-empty pool are dropped when empty. Manually requested blocks are kept.
    /// </remarks>
    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        IBlockProductionTrigger poolTrigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
            .IfPoolIsNotEmpty(txPool);
        return new CompositeBlockProducerRunner(
            new StandardBlockProducerRunner(poolTrigger, blockTree, new NonEmptyBlockProducer(blockProducer)),
            new StandardBlockProducerRunner(manualBlockProductionTrigger, blockTree, blockProducer));
    }

    private sealed class NonEmptyBlockProducer(IBlockProducer blockProducer) : IBlockProducer
    {
        public async Task<Block?> BuildBlock(
            BlockHeader? parentHeader = null,
            IBlockTracer? blockTracer = null,
            PayloadAttributes? payloadAttributes = null,
            IBlockProducer.Flags flags = IBlockProducer.Flags.None,
            CancellationToken cancellationToken = default)
        {
            Block? block = await blockProducer.BuildBlock(parentHeader, blockTracer, payloadAttributes, flags, cancellationToken);
            return block is { Transactions.Length: > 0 } ? block : null;
        }
    }

    private sealed class CompositeBlockProducerRunner(IBlockProducerRunner first, IBlockProducerRunner second) : IBlockProducerRunner
    {
        public void Start()
        {
            first.Start();
            second.Start();
        }

        public async Task StopAsync()
        {
            await first.StopAsync();
            await second.StopAsync();
        }

        public bool IsProducingBlocks(ulong? maxProducingInterval) =>
            first.IsProducingBlocks(maxProducingInterval) || second.IsProducingBlocks(maxProducingInterval);

        public event EventHandler<BlockEventArgs>? BlockProduced
        {
            add
            {
                first.BlockProduced += value;
                second.BlockProduced += value;
            }
            remove
            {
                first.BlockProduced -= value;
                second.BlockProduced -= value;
            }
        }
    }
}
