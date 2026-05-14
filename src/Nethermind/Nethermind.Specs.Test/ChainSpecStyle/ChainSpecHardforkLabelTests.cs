// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using FluentAssertions;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[Parallelizable(ParallelScope.All)]
public class ChainSpecHardforkLabelTests
{
    private static ChainSpec Load(string paramsJson)
    {
        // chainID=1 lets the EIP-6110 deposit-contract fallback resolve when Prague is expanded.
        string json = $$"""
            {
              "name": "Test",
              "engine": { "NethDev": {} },
              "params": {
                "networkID": "0x1",
                "chainID": "0x1",
                {{paramsJson}}
              },
              "genesis": { "seal": { "ethereum": { "nonce": "0x0", "mixHash": "0x0000000000000000000000000000000000000000000000000000000000000000" } }, "difficulty": "0x1", "author": "0x0000000000000000000000000000000000000000", "timestamp": "0x0", "parentHash": "0x0000000000000000000000000000000000000000000000000000000000000000", "extraData": "0x", "gasLimit": "0x1388" },
              "accounts": {}
            }
            """;
        ChainSpecLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        return loader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    [TestCase("shanghai", "0x100", new[]
    {
        nameof(ChainParameters.Eip3651TransitionTimestamp),
        nameof(ChainParameters.Eip3855TransitionTimestamp),
        nameof(ChainParameters.Eip3860TransitionTimestamp),
        nameof(ChainParameters.Eip4895TransitionTimestamp),
    })]
    [TestCase("cancun", "0x65687fd0", new[]
    {
        nameof(ChainParameters.Eip1153TransitionTimestamp),
        nameof(ChainParameters.Eip4788TransitionTimestamp),
        nameof(ChainParameters.Eip4844TransitionTimestamp),
        nameof(ChainParameters.Eip5656TransitionTimestamp),
        nameof(ChainParameters.Eip6780TransitionTimestamp),
    })]
    [TestCase("prague", "0x200", new[]
    {
        nameof(ChainParameters.Eip2537TransitionTimestamp),
        nameof(ChainParameters.Eip2935TransitionTimestamp),
        nameof(ChainParameters.Eip6110TransitionTimestamp),
        nameof(ChainParameters.Eip7002TransitionTimestamp),
        nameof(ChainParameters.Eip7251TransitionTimestamp),
        nameof(ChainParameters.Eip7623TransitionTimestamp),
        nameof(ChainParameters.Eip7702TransitionTimestamp),
    })]
    [TestCase("osaka", "0x300", new[]
    {
        nameof(ChainParameters.Eip7594TransitionTimestamp),
        nameof(ChainParameters.Eip7823TransitionTimestamp),
        nameof(ChainParameters.Eip7825TransitionTimestamp),
        nameof(ChainParameters.Eip7883TransitionTimestamp),
        nameof(ChainParameters.Eip7918TransitionTimestamp),
        nameof(ChainParameters.Eip7934TransitionTimestamp),
        nameof(ChainParameters.Eip7939TransitionTimestamp),
        nameof(ChainParameters.Eip7951TransitionTimestamp),
    })]
    public void Label_expands_to_all_constituent_eips(string labelKey, string labelValueHex, string[] expectedEipProperties)
    {
        ChainSpec spec = Load($"\"{labelKey}\": \"{labelValueHex}\"");

        ulong expected = Convert.ToUInt64(labelValueHex, 16);
        foreach (string propName in expectedEipProperties)
        {
            ulong? actual = (ulong?)typeof(ChainParameters).GetProperty(propName)!.GetValue(spec.Parameters);
            actual.Should().Be(expected, $"{propName} should match the {labelKey} label");
        }
    }

    [Test]
    public void Label_and_redundant_explicit_eip_with_matching_value_is_accepted()
    {
        ChainSpec spec = Load("\"cancun\": \"0x100\", \"eip4844TransitionTimestamp\": \"0x100\"");

        spec.Parameters.Eip4844TransitionTimestamp.Should().Be(0x100);
        spec.Parameters.Eip1153TransitionTimestamp.Should().Be(0x100);
    }

    [Test]
    public void Label_conflicting_with_explicit_eip_throws()
    {
        Action act = () => Load("\"cancun\": \"0x100\", \"eip4844TransitionTimestamp\": \"0x200\"");

        // ChainSpecLoader.Load wraps the inner config exception in InvalidDataException.
        act.Should().Throw<InvalidDataException>()
            .WithInnerException<InvalidConfigurationException>()
            .WithMessage("*Cancun*Eip4844TransitionTimestamp*");
    }

    [Test]
    public void Per_eip_only_chainspec_is_unaffected()
    {
        ChainSpec spec = Load("\"eip4844TransitionTimestamp\": \"0x55\", \"eip4788TransitionTimestamp\": \"0x55\"");

        spec.Parameters.Eip4844TransitionTimestamp.Should().Be(0x55);
        spec.Parameters.Eip4788TransitionTimestamp.Should().Be(0x55);
        // Sibling EIPs not part of this declaration stay null.
        spec.Parameters.Eip1153TransitionTimestamp.Should().BeNull();
    }
}
