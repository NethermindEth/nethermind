// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Xdc.Spec;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

/// <summary>
/// Pins what our chain specs produce for the XDCX special-transaction path on both networks.
/// </summary>
[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcChainSpecTests
{
    [Test]
    public void System_contract_addresses_are_deserialized([Values("xdc.json", "xdc-testnet.json")] string chainSpecFile)
    {
        XdcChainSpecEngineParameters engineParameters = EngineParameters(LoadChainSpec(chainSpecFile));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(engineParameters.MasternodeVotingContract, Is.EqualTo(new Address("0x0000000000000000000000000000000000000088")));
            Assert.That(engineParameters.BlockSignerContract, Is.EqualTo(new Address("0x0000000000000000000000000000000000000089")));
            Assert.That(engineParameters.RandomizeSMCBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000090")));
            Assert.That(engineParameters.XDCXAddressBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000091")));
            Assert.That(engineParameters.TradingStateAddressBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000092")));
            Assert.That(engineParameters.XDCXLendingAddressBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000093")));
            Assert.That(engineParameters.XDCXLendingFinalizedTradeAddressBinary, Is.EqualTo(new Address("0x0000000000000000000000000000000000000094")));
        }
    }

    [Test]
    public void XDCX_flags_flip_on_their_own_blocks([Values("xdc.json", "xdc-testnet.json")] string chainSpecFile)
    {
        ChainSpec chainSpec = LoadChainSpec(chainSpecFile);
        XdcChainSpecEngineParameters engineParameters = EngineParameters(chainSpec);
        XdcChainSpecBasedSpecProvider specProvider = new(chainSpec, engineParameters, LimboLogs.Instance);

        ulong activation = engineParameters.TipXDCX!.Value;
        ulong minerDisable = engineParameters.TIPXDCXMinerDisable!.Value;
        ulong receiverDisable = engineParameters.TIPXDCXReceiverDisable!.Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(specProvider.GetXdcSpec(activation - 1).IsTIPXDCXMiner, Is.False);
            Assert.That(specProvider.GetXdcSpec(activation).IsTIPXDCXMiner, Is.True);
            Assert.That(specProvider.GetXdcSpec(minerDisable - 1).IsTIPXDCXMiner, Is.True);
            Assert.That(specProvider.GetXdcSpec(minerDisable).IsTIPXDCXMiner, Is.False);

            Assert.That(specProvider.GetXdcSpec(activation - 1).IsTIPXDCXReceiver, Is.False);
            Assert.That(specProvider.GetXdcSpec(activation).IsTIPXDCXReceiver, Is.True);
            Assert.That(specProvider.GetXdcSpec(receiverDisable - 1).IsTIPXDCXReceiver, Is.True);
            Assert.That(specProvider.GetXdcSpec(receiverDisable).IsTIPXDCXReceiver, Is.False);
        }
    }

    // Apothem sets the post-TIPUpgradePenalty parole window in its v2 config and mainnet does not, where
    // PenaltyHandler falls back to a single epoch. Both feed a consensus rule, so pin what the files resolve to.
    [TestCase("xdc-testnet.json", 5UL)]
    [TestCase("xdc.json", 0UL)]
    public void Penalty_window_is_taken_from_the_v2_config(string chainSpecFile, ulong expectedLimitPenaltyEpoch)
    {
        ChainSpec chainSpec = LoadChainSpec(chainSpecFile);
        XdcChainSpecEngineParameters engineParameters = EngineParameters(chainSpec);
        XdcChainSpecBasedSpecProvider specProvider = new(chainSpec, engineParameters, LimboLogs.Instance);

        ulong latestRound = engineParameters.V2Configs[^1].SwitchRound;
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(engineParameters.SwitchBlock + 1, latestRound);

        Assert.That(spec.LimitPenaltyEpoch, Is.EqualTo(expectedLimitPenaltyEpoch));
    }

    // The reference client's TestnetChainConfig.PragueBlock. Its PrecompiledContractsPrague adds EIP-2537 at 0x0b-0x11
    // and the point evaluation stub at 0x0a, which no earlier set has, EIP-4844 or not.
    [TestCase("xdc-testnet.json", 71_551_800UL, false)]
    [TestCase("xdc-testnet.json", 84_332_999UL, false)]
    [TestCase("xdc-testnet.json", 84_333_000UL, true)]
    [TestCase("xdc.json", 98_802_000UL, false)]
    public void Prague_activates_at_the_reference_block(string chainSpecFile, ulong block, bool prague)
    {
        ChainSpec chainSpec = LoadChainSpec(chainSpecFile);
        XdcChainSpecBasedSpecProvider specProvider = new(chainSpec, EngineParameters(chainSpec), LimboLogs.Instance);
        IReleaseSpec spec = specProvider.GetXdcSpec(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.IsEip2537Enabled, Is.EqualTo(prague));
            Assert.That(spec.IsEip2935Enabled, Is.EqualTo(prague));
            Assert.That(spec.IsEip7623Enabled, Is.EqualTo(prague));
            Assert.That(spec.IsEip7702Enabled, Is.EqualTo(prague));
            for (byte precompile = 0x0a; precompile <= 0x11; precompile++)
            {
                Assert.That(spec.IsPrecompile(new Address($"0x{precompile:x40}")), Is.EqualTo(prague), $"precompile 0x{precompile:x2}");
            }
        }
    }

    // The reference client's TestnetChainConfig.TIPProportionalRewardBlock; mainnet has not scheduled it.
    [TestCase("xdc-testnet.json", 84_732_999UL, false)]
    [TestCase("xdc-testnet.json", 84_733_000UL, true)]
    [TestCase("xdc.json", 98_802_000UL, false)]
    public void Proportional_reward_activates_at_the_reference_block(string chainSpecFile, ulong block, bool proportional)
    {
        ChainSpec chainSpec = LoadChainSpec(chainSpecFile);
        XdcChainSpecBasedSpecProvider specProvider = new(chainSpec, EngineParameters(chainSpec), LimboLogs.Instance);

        Assert.That(specProvider.GetXdcSpec(block).IsTipProportionalRewardEnabled, Is.EqualTo(proportional));
    }

    private static ChainSpec LoadChainSpec(string chainSpecFile) =>
        new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(chainSpecFile);

    private static XdcChainSpecEngineParameters EngineParameters(ChainSpec chainSpec) =>
        chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<XdcChainSpecEngineParameters>();
}
