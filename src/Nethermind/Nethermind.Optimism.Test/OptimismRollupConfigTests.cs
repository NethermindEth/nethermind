// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.Cl.Rpc;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismRollupConfigTests
{
    [Test]
    public void Op_mainnet_rollup_config_uses_rollup_genesis_and_contract_fields()
    {
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("chainspec/op-mainnet.json.zst");
        CLChainSpecEngineParameters clParameters = chainSpec.EngineChainSpecParametersProvider
            .GetChainSpecParameters<CLChainSpecEngineParameters>();
        OptimismChainSpecEngineParameters engineParameters = chainSpec.EngineChainSpecParametersProvider
            .GetChainSpecParameters<OptimismChainSpecEngineParameters>();

        OptimismRollupConfig config = OptimismRollupConfig.Build(clParameters, engineParameters, chainSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.Genesis.L1.Number, Is.EqualTo(clParameters.L1GenesisNumber));
            Assert.That(config.Genesis.L2.Number, Is.EqualTo(chainSpec.Genesis?.Number));
            Assert.That(config.BatchInboxAddress, Is.EqualTo(clParameters.BatcherInboxAddress));
            Assert.That(config.DepositContractAddress, Is.EqualTo(clParameters.OptimismPortalProxy));
        }
    }
}
