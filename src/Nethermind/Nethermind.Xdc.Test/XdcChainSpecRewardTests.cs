// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Spec;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

/// <summary>
/// Covers reading XDPoS rewards, which the chainspec states in XDC, and the boundary that keeps
/// that unit convention inside <c>engine.XDPoS.params</c>.
/// </summary>
[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcChainSpecRewardTests
{
    /// <summary>The only Apothem v2 config that sets rewards.</summary>
    private const ulong ApothemRewardSwitchRound = 27_360_000;

    private const string RewardPlaceholder = "$REWARD$";
    private const string CoreParamsPlaceholder = "$CORE_PARAMS$";
    private const string EnginePeriodPlaceholder = "$PERIOD$";

    private const string ChainSpecTemplate = $$"""
        {
          "name": "xdc-reward-test",
          "engine": {
            "XDPoS": {
              "params": {
                "period": {{EnginePeriodPlaceholder}},
                "epoch": 900,
                "masternodeReward": {{RewardPlaceholder}},
                "v2Configs": [
                  {
                    "SwitchRound": 0,
                    "MasternodeReward": {{RewardPlaceholder}}
                  }
                ]
              }
            }
          },
          "params": { "chainId": 50{{CoreParamsPlaceholder}} }
        }
        """;

    /// <remarks>
    /// The three Apothem amounts are the ones that pin the conversion: the reference scales the
    /// float64 through a 64-bit significand and truncates, so 63.42 XDC is 63420000000000001704 wei
    /// rather than a round 6.342e19. Getting this wrong changes what validators are paid.
    /// </remarks>
    [TestCase("63.42", "63420000000000001704", TestName = "Masternode reward")]
    [TestCase("50.27", "50270000000000003128", TestName = "Protector reward")]
    [TestCase("25.13", "25129999999999999006", TestName = "Observer reward")]
    [TestCase("6.342e1", "63420000000000001704", TestName = "Exponent notation is the same amount")]
    [TestCase("1", "1000000000000000000", TestName = "Whole XDC")]
    [TestCase("2.5", "2500000000000000000", TestName = "Exactly representable fraction")]
    [TestCase("0.5", "500000000000000000", TestName = "Fraction below one")]
    [TestCase("1e-18", "1", TestName = "One wei")]
    [TestCase("1e-19", "0", TestName = "Below one wei truncates to zero")]
    [TestCase("0", "0", TestName = "Zero")]
    [TestCase("0.0", "0", TestName = "Zero with a fraction")]
    public void Reward_stated_in_xdc_is_converted_to_wei(string literal, string expectedWei)
    {
        XdcChainSpecEngineParameters parameters = LoadEngineParameters(literal);

        UInt256 wei = UInt256.Parse(expectedWei);
        Assert.Multiple(() =>
        {
            Assert.That(parameters.MasternodeReward, Is.EqualTo(wei));
            Assert.That(parameters.V2Configs[0].MasternodeReward, Is.EqualTo(wei));
        });
    }

    [TestCase("-1", TestName = "Negative")]
    [TestCase("-0.0", TestName = "Negative zero")]
    [TestCase("1e60", TestName = "Beyond UInt256 once scaled")]
    [TestCase("\"0x37f0e6c9e9dd0e0000\"", TestName = "Hex string, which would have to mean wei")]
    [TestCase("\"63.42\"", TestName = "Quoted amount")]
    public void Reward_that_is_not_an_xdc_amount_is_rejected(string literal) =>
        Assert.That(() => LoadEngineParameters(literal), Throws.TypeOf<InvalidDataException>());

    /// <summary>
    /// The conversion is opt-in per property, so an XDPoS field that was not annotated keeps the
    /// shared converter's behaviour.
    /// </summary>
    [Test]
    public void Fractional_value_in_an_unannotated_engine_field_is_rejected() =>
        Assert.That(() => LoadEngineParameters("1", period: "2.0"), Throws.TypeOf<InvalidDataException>());

    /// <summary>
    /// The converter is attached to properties owned by <c>Nethermind.Xdc</c>, so nothing outside the
    /// XDPoS engine section gains either the XDC unit or tolerance for a fractional number.
    /// </summary>
    [TestCase(", \"eip150Transition\": 2.0", TestName = "Transition block")]
    [TestCase(", \"terminalTotalDifficulty\": 1e18", TestName = "Terminal total difficulty")]
    public void Fractional_value_outside_the_engine_section_is_rejected(string coreParams) =>
        Assert.That(() => LoadEngineParameters("1", coreParams: coreParams), Throws.TypeOf<InvalidDataException>());

    /// <summary>
    /// Pins the shipped Apothem chainspec to the wei amounts it resolved to before the rewards were
    /// restated in XDC.
    /// </summary>
    [Test]
    public void Shipped_testnet_rewards_are_unchanged()
    {
        V2ConfigParams config = LoadShippedTestnetRewardConfig();

        Assert.Multiple(() =>
        {
            Assert.That(config.MasternodeReward, Is.EqualTo(UInt256.Parse("63420000000000001704")));
            Assert.That(config.ProtectorReward, Is.EqualTo(UInt256.Parse("50270000000000003128")));
            Assert.That(config.ObserverReward, Is.EqualTo(UInt256.Parse("25129999999999999006")));
        });
    }

    /// <summary>
    /// <see cref="V2ConfigParams"/> doubles as the <c>XDPoS_networkInformation</c> response DTO, so
    /// the reward properties are serialized as well as read. The response reports wei, unchanged by
    /// the chainspec moving to XDC.
    /// </summary>
    [Test]
    public void Network_information_response_reports_rewards_in_wei()
    {
        NetworkInformation response = new()
        {
            ConsensusConfigs = new XDPoSConfig { V2Configs = [LoadShippedTestnetRewardConfig()] }
        };

        string json = new EthereumJsonSerializer().Serialize(response);

        // The three rewards as the QUANTITY the endpoint has always emitted.
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("0x3702119fc874606a8"));
            Assert.That(json, Does.Contain("0x2b9a2eaa87ae30c38"));
            Assert.That(json, Does.Contain("0x15cbfb1db0590fc1e"));
        });
    }

    private static V2ConfigParams LoadShippedTestnetRewardConfig()
    {
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("chainspec/xdc-testnet.json");

        XdcChainSpecEngineParameters parameters = chainSpec.EngineChainSpecParametersProvider
            .GetChainSpecParameters<XdcChainSpecEngineParameters>();

        foreach (V2ConfigParams config in parameters.V2Configs)
        {
            if (config.SwitchRound == ApothemRewardSwitchRound) return config;
        }

        throw new AssertionException($"No v2 config at switch round {ApothemRewardSwitchRound}");
    }

    private static XdcChainSpecEngineParameters LoadEngineParameters(string reward, string coreParams = "", string period = "2")
    {
        string json = ChainSpecTemplate
            .Replace(RewardPlaceholder, reward)
            .Replace(CoreParamsPlaceholder, coreParams)
            .Replace(EnginePeriodPlaceholder, period);

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        ChainSpec chainSpec = new ChainSpecLoader(new EthereumJsonSerializer(), LimboLogs.Instance).Load(stream);

        return chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<XdcChainSpecEngineParameters>();
    }
}
