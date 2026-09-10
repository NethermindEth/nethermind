// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Blocks;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Rpc;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Qbft;

/// <summary>
/// IBFT 2.0: everything needed to follow and validate the chain, and nothing to take part in consensus.
/// </summary>
/// <remarks>
/// The header rules, hashing, validator voting and proposer rotation are shared with QBFT, so this
/// registers the same <c>Bft*</c> components with the IBFT 2.0 extra-data codec. Consensus
/// participation would additionally need Besu's <c>IBF/1</c> sub-protocol and its message set, which
/// this engine does not implement, so no block producer, sealer or protocol handler is registered and
/// the node follows the chain as an observer.
/// </remarks>
public class Ibft2Module : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .Map<Ibft2ChainSpecEngineParameters, ChainSpec>(static chainSpec =>
                chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<Ibft2ChainSpecEngineParameters>())
            .AddSingleton<IBftExtraDataCodecSelector>(Ibft2OnlyCodecSelector.Instance)
            .AddSingleton<BftForksSchedule, Ibft2ChainSpecEngineParameters, ISpecProvider>(static (parameters, specProvider) =>
                BftForksSchedule.Create(parameters, specProvider.TimestampFork))
            .AddSingleton<EpochManager, Ibft2ChainSpecEngineParameters>(static parameters => new EpochManager(parameters.EpochLength))

            // Header typing: registered eagerly because the RLP registry is global.
            .AddModule(new Ibft2HeaderModule())

            .AddSingleton<BftBlockInterface>()

            // Validator sets. IBFT 2.0 has no validator contract mode, so the block-header vote tally is the only source.
            .AddSingleton<IValidatorProvider, IBlockTree, EpochManager, BftBlockInterface>(
                static (blockTree, epochManager, blockInterface) => BlockValidatorProvider.NonForking(blockTree, epochManager, blockInterface))
            .AddSingleton<IProposerSelector, BftProposerSelector>()

            // Validation
            .AddSingleton<ISealValidator, BftSealValidator>()
            .AddSingleton<IHeaderValidator, BftHeaderValidator>()
            .AddSingleton<IUnclesValidator, BftUnclesValidator>()
            .AddDecorator<ITxValidator, BftPerTxGasLimitTxValidator>()
            .AddDecorator<IBlockValidator, BftPerTxGasLimitBlockValidator>()
            .AddLast<IBlockPreprocessorStep, BftAuthorRecoveryStep>()

            // Rewards and gas
            .AddSingleton<IRewardCalculatorSource, BftRewardCalculator>()
            .AddSingleton<IGasLimitCalculator, BftGasLimitCalculator>()

            // JSON-RPC
            .RegisterSingletonJsonRpcModule<IIbftRpcModule, Ibft2RpcModule>();
    }

    /// <summary>Types headers with the IBFT 2.0 codec so their hash is the BFT on-chain digest.</summary>
    private sealed class Ibft2HeaderModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            BftHeaderDecoder headerDecoder = new(Ibft2OnlyCodecSelector.Instance);
            BlockDecoder blockDecoder = new(headerDecoder);
            BlockBodyDecoder blockBodyDecoder = new(headerDecoder);
            Rlp.RegisterDecoder(typeof(BlockHeader), headerDecoder);
            Rlp.RegisterDecoder(typeof(Block), blockDecoder);
            Rlp.RegisterDecoder(typeof(BlockBody), blockBodyDecoder);

            builder
                .AddSingleton<IHeaderDecoder>(headerDecoder)
                .AddSingleton(blockDecoder)
                .AddSingleton(blockBodyDecoder)
                .AddDecorator<IGenesisBuilder, BftGenesisBuilder>();
        }
    }
}
