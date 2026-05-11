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

    [Test]
    public void Cancun_label_expands_to_all_constituent_eips()
    {
        ChainSpec spec = Load("\"cancun\": \"0x65687fd0\"");

        ulong expected = 0x65687fd0;
        spec.Parameters.Eip1153TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip4788TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip4844TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip5656TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip6780TransitionTimestamp.Should().Be(expected);
    }

    [Test]
    public void Shanghai_label_expands_to_all_constituent_eips()
    {
        ChainSpec spec = Load("\"shanghai\": \"0x100\"");

        ulong expected = 0x100;
        spec.Parameters.Eip3651TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip3855TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip3860TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip4895TransitionTimestamp.Should().Be(expected);
    }

    [Test]
    public void Prague_label_expands_to_all_constituent_eips()
    {
        ChainSpec spec = Load("\"prague\": \"0x200\"");

        ulong expected = 0x200;
        spec.Parameters.Eip2537TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip2935TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip6110TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7002TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7251TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7623TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7702TransitionTimestamp.Should().Be(expected);
    }

    [Test]
    public void Osaka_label_expands_to_all_constituent_eips()
    {
        ChainSpec spec = Load("\"osaka\": \"0x300\"");

        ulong expected = 0x300;
        spec.Parameters.Eip7594TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7823TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7825TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7883TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7918TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7934TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7939TransitionTimestamp.Should().Be(expected);
        spec.Parameters.Eip7951TransitionTimestamp.Should().Be(expected);
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
