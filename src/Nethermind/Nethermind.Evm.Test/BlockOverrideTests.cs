// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class BlockOverrideTests
{
    // Zero is a valid prevRandao: an override of zero must reach PREVRANDAO, not fall back to the base block's mix hash.
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000005")]
    public void A_prevRandao_override_reaches_the_opcode(string prevRandao)
    {
        Hash256 overrideValue = new(prevRandao);
        BlockHeader header = Build.A.BlockHeader
            .WithMixHash(new Hash256("0x0000000000000000000000000000000000000000000000000000000000000007"))
            .TestObject;
        header.IsPostMerge = true;

        BlockHeader result = new BlockOverride { PrevRandao = overrideValue }.ApplyTo(header);

        Assert.That(new BlockExecutionContext(result, Osaka.Instance).PrevRandao, Is.EqualTo(overrideValue.ValueHash256));
    }

    [Test]
    public void No_prevRandao_override_keeps_the_mix_hash()
    {
        Hash256 mixHash = new("0x0000000000000000000000000000000000000000000000000000000000000007");
        BlockHeader header = Build.A.BlockHeader.WithMixHash(mixHash).TestObject;
        header.IsPostMerge = true;

        BlockHeader result = new BlockOverride().ApplyTo(header);

        Assert.That(new BlockExecutionContext(result, Osaka.Instance).PrevRandao, Is.EqualTo(mixHash.ValueHash256));
    }
}
