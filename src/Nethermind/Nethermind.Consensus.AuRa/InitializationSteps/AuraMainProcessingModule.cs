// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class AuraMainProcessingModule(
    IAbiEncoder abiEncoder,
    IGasPriceOracle gasPriceOracle,
    IReadOnlyTxProcessingEnvFactory envFactory,
    AuRaChainSpecEngineParameters chainSpecAuRa
) : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder) => builder.AddSingleton<IAuRaValidator, AuRaNethermindApi, IWorldState, ITransactionProcessor, IReceiptStorage, IAuRaBlockFinalizationManager>(CreateAuRaValidator);

    private IAuRaValidator CreateAuRaValidator(AuRaNethermindApi api, IWorldState worldState, ITransactionProcessor transactionProcessor, IReceiptStorage receiptStorage, IAuRaBlockFinalizationManager finalizationManager)
    {

        IAuRaValidator validator = new AuRaValidatorFactory(
                abiEncoder,
                worldState,
                transactionProcessor,
                api.BlockTree!,
                envFactory.Create(),
                receiptStorage,
                api.ValidatorStore,
                finalizationManager,
                new TxPoolSender(api.TxPool!, new TxSealer(api.EngineSigner!, api.Timestamper), api.NonceManager!, api.EthereumEcdsa),
                api.TxPool!,
                api.Config<IBlocksConfig>(),
                api.LogManager,
                api.EngineSigner!,
                api.SpecProvider,
                gasPriceOracle,
                api.ReportingContractValidatorCache,
                chainSpecAuRa.PosdaoTransition)
            .CreateValidatorProcessor(chainSpecAuRa.Validators, api.BlockTree.Head?.Header);

        if (validator is IDisposable disposableValidator)
        {
            api.DisposeStack.Push(disposableValidator);
        }

        return validator;
    }
}
