// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.Stats.Model;
using NUnit.Framework;

using SealEngineType = Nethermind.Core.SealEngineType;

namespace Nethermind.EthereumClassic.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class EthereumClassicPluginTests
{
    /// <summary>
    /// eth/69 (EIP-7642) removes TotalDifficulty from the Status message,
    /// making it incompatible with PoW chains like ETC/Mordor.
    /// This test ensures DefaultCapabilities never includes eth/69.
    /// </summary>
    [Test]
    public void DefaultCapabilities_does_not_include_eth69()
    {
        Capability eth69 = new(Protocol.Eth, 69);
        bool containsEth69 = ProtocolsManager.DefaultCapabilities.Contains(eth69);

        Assert.That(containsEth69, Is.False, "DefaultCapabilities should not include eth/69 (incompatible with PoW)");
    }

    [Test]
    public void Plugin_is_enabled_for_etchash_chain()
    {
        ChainSpec chainSpec = CreateEtchashChainSpec();
        EthereumClassicPlugin plugin = new(chainSpec);

        Assert.That(plugin.Enabled, Is.True);
    }

    [Test]
    public void Plugin_is_disabled_for_non_etchash_chain()
    {
        ChainSpec chainSpec = new()
        {
            SealEngineType = SealEngineType.Ethash,
            EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev
        };
        EthereumClassicPlugin plugin = new(chainSpec);

        Assert.That(plugin.Enabled, Is.False);
    }

    private static ChainSpec CreateEtchashChainSpec()
    {
        return new ChainSpec
        {
            SealEngineType = SealEngineType.Ethash,
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new EtchashChainSpecEngineParameters
                {
                    Ecip1099Transition = 11700000,
                    Ecip1017EraRounds = 5000000
                })
        };
    }
}
