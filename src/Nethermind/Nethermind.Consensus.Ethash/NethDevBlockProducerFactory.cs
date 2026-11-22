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
    /// The pool can hold transactions the producer never includes (nonce gap, fee below the base-fee floor or
    /// the min gas price), so a block triggered by a non-empty pool is dropped when it is empty and does not
    /// lower the base fee. Empty blocks that lower the base fee are kept, so a tx priced below the base fee
    /// is still included once the base fee drops to its fee. Manually requested blocks are kept.
    /// </remarks>
    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        IBlockProductionTrigger poolTrigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
            .IfPoolIsNotEmpty(txPool);
        return new CompositeBlockProducerRunner(
            new StandardBlockProducerRunner(poolTrigger, blockTree, new IdleBlockDroppingProducer(blockProducer)),
            new StandardBlockProducerRunner(manualBlockProductionTrigger, blockTree, blockProducer));
    }

    private sealed class IdleBlockDroppingProducer(IBlockProducer blockProducer) : IBlockProducer
    {
        public async Task<Block?> BuildBlock(
            BlockHeader? parentHeader = null,
            IBlockTracer? blockTracer = null,
            PayloadAttributes? payloadAttributes = null,
            IBlockProducer.Flags flags = IBlockProducer.Flags.None,
            CancellationToken cancellationToken = default)
        {
            Block? block = await blockProducer.BuildBlock(parentHeader, blockTracer, payloadAttributes, flags, cancellationToken);
            return block is not null && (block.Transactions.Length > 0 || block.BaseFeePerGas < parentHeader?.BaseFeePerGas) ? block : null;
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
            try
            {
                await first.StopAsync();
            }
            finally
            {
                await second.StopAsync();
            }
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
