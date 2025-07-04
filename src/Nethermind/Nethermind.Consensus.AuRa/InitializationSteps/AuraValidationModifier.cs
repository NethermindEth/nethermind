// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

// These components are configured for RPC and main validation block processor, but not for block producer's
// block processor
public class AuraValidationModifier(
    AuRaChainSpecEngineParameters parameters,
    ISpecProvider specProvider,
    TxAuRaFilterBuilders txAuRaFilterBuilders,
    AuRaGasLimitOverrideFactory gasLimitOverrideFactory
) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        ITxFilter txFilter = txAuRaFilterBuilders.CreateAuRaTxFilter(new ServiceTxFilter(specProvider));

        IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = parameters.RewriteBytecode;
        ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

        AuRaContractGasLimitOverride? gasLimitOverride = gasLimitOverrideFactory.GetGasLimitCalculator();

        builder.AddSingleton(txFilter);
        if (contractRewriter is not null) builder.AddSingleton(contractRewriter);
        if (gasLimitOverride is not null) builder.AddSingleton(gasLimitOverride);
    }
}
