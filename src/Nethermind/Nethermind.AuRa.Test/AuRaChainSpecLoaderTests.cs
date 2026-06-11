// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.AuRa.Test;

public class AuRaChainSpecLoaderTests
{
    [Test]
    public void Upgrades_genesis_with_authorityRound_seal()
    {
        byte[] signature = new byte[65];
        signature[0] = 0xab;
        ChainSpec chainSpec = new()
        {
            Genesis = Build.A.Block.WithDifficulty(131072).TestObject,
            CustomSeal = ParseSeal($"{{\"authorityRound\":{{\"step\":\"0x2a\",\"signature\":\"{signature.ToHexString(withZeroX: true)}\"}}}}"),
        };

        AuRaChainSpecLoader.ProcessChainSpec(chainSpec);

        Assert.That(chainSpec.Genesis.Header, Is.InstanceOf<AuRaBlockHeader>());
        AuRaBlockHeader header = (AuRaBlockHeader)chainSpec.Genesis.Header;
        Assert.That(header.AuRaStep, Is.EqualTo(42L));
        Assert.That(header.AuRaSignature, Is.EqualTo(signature));
    }

    [Test]
    public void Noop_without_authorityRound_seal()
    {
        Block genesis = Build.A.Block.TestObject;
        ChainSpec chainSpec = new()
        {
            Genesis = genesis,
            CustomSeal = ParseSeal("""{"someOtherEngine":{"foo":"0x1"}}"""),
        };

        AuRaChainSpecLoader.ProcessChainSpec(chainSpec);

        Assert.That(chainSpec.Genesis, Is.SameAs(genesis));
        Assert.That(chainSpec.Genesis.Header, Is.Not.InstanceOf<AuRaBlockHeader>());
    }

    private static Dictionary<string, JsonElement> ParseSeal(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
