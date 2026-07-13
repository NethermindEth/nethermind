// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Block producer environment for AuRa (pre-merge) sealing.
/// </summary>
/// <remarks>
/// On top of the standard producer wiring it registers the sealing <see cref="IAuRaValidator"/> (shared between
/// <see cref="AuRaBlockProcessor"/> and the tx source), the producer tx filter, the block gas limit contract
/// override and contract rewriter when configured, and wraps the tx pool source with posdao/randomness
/// transaction sources.
/// </remarks>
internal sealed class AuRaBlockProducerEnvFactory(
    ILifetimeScope rootLifetime,
    IWorldStateManager worldStateManager,
    AuRaTxPoolTxSourceFactory txPoolTxSourceFactory,
    ISpecProvider specProvider,
    AuRaChainSpecEngineParameters parameters,
    IBlocksConfig blocksConfig,
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
    IValidatorStore validatorStore,
    IAuRaBlockFinalizationManager finalizationManager,
    ISigner engineSigner,
    IGasPriceOracle gasPriceOracle,
    ReportingContractBasedValidator.Cache reportingContractValidatorCache,
    IAbiEncoder abiEncoder,
    IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
    TxAuRaFilterBuilders txAuRaFilterBuilders,
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey protectedPrivateKey,
    ICryptoRandom cryptoRandom,
    ITimestamper timestamper,
    IStateReader stateReader,
    AuRaGasLimitOverrideFactory gasLimitOverrideFactory,
    ILogManager logManager)
    : BlockProducerEnvFactory(rootLifetime, worldStateManager, txPoolTxSourceFactory)
{
    protected override ContainerBuilder ConfigureBuilder(ContainerBuilder builder)
    {
        builder = base.ConfigureBuilder(builder)
            .AddScoped<IWithdrawalProcessor>(NullWithdrawalProcessor.Instance)
            .AddScoped<ITxFilter>(txAuRaFilterBuilders.CreateAuRaTxFilter(new LocalTxFilter(engineSigner)))
            .AddScoped<IAuRaValidator, IWorldState, ITransactionProcessor>(CreateAuRaValidator)
            .AddDecorator<ITxSource>(WrapTxSourceForProducer);

        AuRaContractGasLimitOverride? gasLimitOverride = gasLimitOverrideFactory.GetGasLimitCalculator();
        if (gasLimitOverride is not null)
        {
            builder.AddScoped(gasLimitOverride);
        }

        IDictionary<ulong, IDictionary<Address, byte[]>> rewriteBytecode = parameters.RewriteBytecode;
        (ulong, Address, byte[])[] rewriteBytecodeTimestamp = [.. parameters.RewriteBytecodeTimestampParsed];
        if (rewriteBytecode?.Count > 0 || rewriteBytecodeTimestamp?.Length > 0)
        {
            builder.AddScoped(new ContractRewriter(rewriteBytecode, rewriteBytecodeTimestamp));
        }

        return builder;
    }

    private IAuRaValidator CreateAuRaValidator(IWorldState worldState, ITransactionProcessor transactionProcessor) =>
        new AuRaValidatorFactory(abiEncoder,
                worldState,
                transactionProcessor,
                blockTree,
                readOnlyTxProcessingEnvFactory.Create(),
                receiptStorage,
                validatorStore,
                finalizationManager,
                NullTxSender.Instance,
                NullTxPool.Instance,
                blocksConfig,
                logManager,
                engineSigner,
                specProvider,
                gasPriceOracle,
                reportingContractValidatorCache,
                parameters.PosdaoTransition,
                forSealing: true)
            .CreateValidatorProcessor(parameters.Validators, blockTree.Head?.Header);

    private ITxSource WrapTxSourceForProducer(IComponentContext ctx, ITxSource txPoolSource)
    {
        IList<ITxSource> txSources = [txPoolSource];
        bool needSigner = false;

        if (parameters.PosdaoTransition != AuRaChainSpecEngineParameters.TransitionDisabled
            && ctx.Resolve<IAuRaValidator>() is ITxSource validatorSource)
        {
            txSources.Insert(0, validatorSource);
            needSigner = true;
        }

        IDictionary<ulong, Address>? randomnessContractAddress = parameters.RandomnessContractAddress;
        if (randomnessContractAddress?.Any() == true)
        {
            RandomContractTxSource randomContractTxSource = new(
                randomnessContractAddress
                    .Select(kvp => new RandomContract(
                        abiEncoder,
                        kvp.Value,
                        readOnlyTxProcessingEnvFactory.Create(),
                        kvp.Key,
                        engineSigner))
                    .ToArray<IRandomContract>(),
                new EciesCipher(cryptoRandom),
                engineSigner,
                protectedPrivateKey,
                cryptoRandom,
                logManager);

            txSources.Insert(0, randomContractTxSource);
            needSigner = true;
        }

        ITxSource txSource = txSources.Count > 1 ? new CompositeTxSource(txSources.ToArray()) : txSources[0];

        if (needSigner)
        {
            TxSealer transactionSealer = new(engineSigner, timestamper);
            txSource = new GeneratedTxSource(txSource, transactionSealer, stateReader, logManager);
        }

        ITxFilter? txPermissionFilter = txAuRaFilterBuilders.CreateTxPermissionFilter();
        if (txPermissionFilter is not null)
        {
            // we now only need to filter generated transactions here, as regular ones are filtered on TxPoolTxSource filter based on CreateTxSourceFilter method
            txSource = new FilteredTxSource<GeneratedTransaction>(txSource, txPermissionFilter, logManager, specProvider, blocksConfig);
        }

        return txSource;
    }
}
