// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Blocks;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Contracts;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Consensus.Qbft.Rpc;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.OverridableEnv;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Qbft;

public class QbftModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .Map<QbftChainSpecEngineParameters, ChainSpec>(static chainSpec =>
                chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<QbftChainSpecEngineParameters>())
            .AddSingleton<IBftExtraDataCodecSelector, QbftChainSpecEngineParameters>(static parameters =>
                parameters.StartBlock is { } startBlock ? new MigrationCodecSelector(startBlock) : QbftOnlyCodecSelector.Instance)
            .AddSingleton<QbftForksSchedule, QbftChainSpecEngineParameters, ISpecProvider>(static (parameters, specProvider) =>
                QbftForksSchedule.Create(parameters, specProvider.TimestampFork))
            .AddSingleton<EpochManager, QbftChainSpecEngineParameters>(static parameters =>
                new EpochManager(parameters.EpochLength, (long)(parameters.StartBlock ?? 0), parameters.Ibft2?.EpochLength))

            // Header typing: registered eagerly because the RLP registry is global.
            .AddModule(new QbftHeaderModuleFromParameters())

            .AddSingleton<QbftBlockInterface>()
            .AddSingleton<QbftMessageCodec, BlockDecoder>(static blockDecoder => new QbftMessageCodec(blockDecoder))

            // Validator sets
            .AddSingleton<IValidatorContract, IAbiEncoder, IReadOnlyTxProcessingEnvFactory>(static (abiEncoder, envFactory) =>
                new ValidatorContract(abiEncoder, envFactory.Create()))
            .AddSingleton<IValidatorProvider, IBlockTree, EpochManager, QbftBlockInterface, QbftForksSchedule, IValidatorContract>(
                static (blockTree, epochManager, blockInterface, forksSchedule, validatorContract) => new ForkingValidatorProvider(
                    blockTree,
                    forksSchedule,
                    BlockValidatorProvider.Forking(blockTree, epochManager, blockInterface, forksSchedule),
                    new TransactionValidatorProvider(blockTree, validatorContract, forksSchedule)))
            .AddSingleton<IProposerSelector, BftProposerSelector>()
            .AddSingleton<ValidatorModeTransitionLogger>()

            // Validation
            .AddSingleton<ISealValidator, QbftSealValidator>()
            .AddSingleton<IHeaderValidator, QbftHeaderValidator>()
            .AddSingleton<IUnclesValidator, QbftUnclesValidator>()
            .AddDecorator<ITxValidator, QbftPerTxGasLimitTxValidator>()
            .AddDecorator<IBlockValidator, QbftPerTxGasLimitBlockValidator>()
            .AddLast<IBlockPreprocessorStep, QbftAuthorRecoveryStep>()
            .AddSingleton<IOverridableEnv<QbftBlockValidatorAdapter.ProcessingEnv>>(static ctx => CreateProposalProcessingEnv(ctx))
            .AddSingleton<IQbftBlockValidator, QbftBlockValidatorAdapter>()
            .AddSingleton<IQbftBlockImporter, QbftBlockImporter>()

            // Rewards, gas, sealing
            .AddSingleton<IRewardCalculatorSource, QbftRewardCalculator>()
            .AddSingleton<IGasLimitCalculator, QbftGasLimitCalculator>()
            .AddSingleton<ISealer, QbftSealer>()
            .AddSingleton<IBlockProductionPolicy>(AlwaysStartBlockProductionPolicy.Instance)

            // Consensus loop
            .AddSingleton<BftEventQueue, QbftChainSpecEngineParameters, ILogManager>(static (parameters, logManager) =>
                new BftEventQueue(parameters.MessageQueueLimit, logManager))
            .Bind<IBftEventQueue, BftEventQueue>()
            .AddSingleton<ValidatorPeers>()
            .AddSingleton<QbftConsensusStatus>()
            .AddSingleton<QbftBlockProducerFactory>()
            .Bind<IBlockProducerFactory, QbftBlockProducerFactory>()
            .Bind<IBlockProducerRunnerFactory, QbftBlockProducerFactory>()

            // devp2p istanbul/100
            .AddProtocolHandler<IstanbulProtocolHandler>()
            .AddMessageSerializer<ProposalWireMessage, ProposalWireMessageSerializer>()
            .AddMessageSerializer<PrepareWireMessage, PrepareWireMessageSerializer>()
            .AddMessageSerializer<CommitWireMessage, CommitWireMessageSerializer>()
            .AddMessageSerializer<RoundChangeWireMessage, RoundChangeWireMessageSerializer>()
            .AddLast<IP2PCapabilityResolver, QbftP2PCapabilityResolver>()

            // JSON-RPC
            .RegisterSingletonJsonRpcModule<IQbftRpcModule, QbftRpcModule>();
    }

    /// <summary>Loads <see cref="QbftHeaderModule"/> with the codec selector derived from the chainspec once it is available.</summary>
    /// <summary>
    /// Proposed blocks are executed on the parent's state in a throwaway overridable world scope with its own
    /// processing chain, the same way receipt regeneration and <c>eth_call</c> build theirs; the typed
    /// <see cref="IOverridableEnv{T}"/> is only resolvable inside that scope.
    /// </summary>
    private static IOverridableEnv<QbftBlockValidatorAdapter.ProcessingEnv> CreateProposalProcessingEnv(IComponentContext ctx)
    {
        IOverridableEnv env = ctx.Resolve<IOverridableEnvFactory>().Create();
        ILifetimeScope rootScope = ctx.Resolve<ILifetimeScope>();
        ILifetimeScope envScope = rootScope.BeginLifetimeScope(builder => builder
            .AddModule(ctx.Resolve<IBlockValidationModule[]>())
            .AddScoped<QbftBlockValidatorAdapter.ProcessingEnv>()
            .AddModule(env));
        rootScope.Disposer.AddInstanceForDisposal(envScope);
        return envScope.Resolve<IOverridableEnv<QbftBlockValidatorAdapter.ProcessingEnv>>();
    }

    private sealed class QbftHeaderModuleFromParameters : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);
            builder.RegisterBuildCallback(static scope =>
            {
                // The global RLP decoder for BlockHeader must be in place before any block is decoded.
                IBftExtraDataCodecSelector codecs = scope.Resolve<IBftExtraDataCodecSelector>();
                QbftHeaderDecoder headerDecoder = new(codecs);
                Rlp.RegisterDecoder(typeof(BlockHeader), headerDecoder);
                Rlp.RegisterDecoder(typeof(Block), new BlockDecoder(headerDecoder));
                Rlp.RegisterDecoder(typeof(BlockBody), new BlockBodyDecoder(headerDecoder));
            });

            builder
                .AddSingleton<IHeaderDecoder, IBftExtraDataCodecSelector>(static codecs => new QbftHeaderDecoder(codecs))
                .AddSingleton<BlockDecoder, IHeaderDecoder>(static headerDecoder => new BlockDecoder(headerDecoder))
                .AddSingleton<BlockBodyDecoder, IHeaderDecoder>(static headerDecoder => new BlockBodyDecoder(headerDecoder))
                .AddDecorator<IGenesisBuilder, QbftGenesisBuilder>();
        }
    }
}
