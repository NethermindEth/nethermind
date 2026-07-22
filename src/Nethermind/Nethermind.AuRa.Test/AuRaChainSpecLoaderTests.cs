// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
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

    [Test]
    public void Preserves_pos_genesis_shape_when_terminal_total_difficulty_is_zero()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters { TerminalTotalDifficulty = UInt256.Zero },
            Genesis = new Block(Build.A.BlockHeader
                .WithParentHash(Keccak.Zero)
                .WithStateRoot(new Hash256("0x2bb23ed248ecca9e940055284bae1c80d9f924a4ce1ca8710b46b06c43f3c548"))
                .WithTransactionsRoot(Keccak.EmptyTreeHash)
                .WithReceiptsRoot(Keccak.EmptyTreeHash)
                .WithDifficulty(UInt256.Zero)
                .WithNumber(0)
                .WithGasLimit(100_000_000)
                .WithGasUsed(0)
                .WithTimestamp(0)
                .WithExtraData(System.Text.Encoding.UTF8.GetBytes("hivechain"))
                .WithMixHash(Keccak.Zero)
                .WithNonce(0)
                .WithBaseFee(1_000_000_000)
                .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
                .TestObject),
            CustomSeal = ParseSeal("{\"authorityRound\":{\"step\":\"0x0\",\"signature\":\"0x" + new string('0', 130) + "\"}}"),
        };

        AuRaChainSpecLoader.ProcessChainSpec(chainSpec);
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.IsPostMerge(Arg.Any<BlockHeader>()).Returns(callInfo =>
        {
            callInfo.Arg<BlockHeader>().IsPostMerge = true;
            return true;
        });
        Block genesis = new AuRaGenesisBuilder(new FixedGenesisBuilder(chainSpec.Genesis), poSSwitcher).Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(genesis.Header, Is.Not.InstanceOf<AuRaBlockHeader>());
            Assert.That(genesis.Header.IsPostMerge, Is.True);
            Assert.That(genesis.Header.CalculateHash(), Is.EqualTo(new Hash256("0x9469153ba75532411cbad308fadd1206ec5c919ef4a463549c9af05a8bad5641")));
        }

        poSSwitcher.Received(1).IsPostMerge(genesis.Header);
    }

    private sealed class FixedGenesisBuilder(Block genesis) : IGenesisBuilder
    {
        public Block Build() => genesis;
    }

    private static Dictionary<string, JsonElement> ParseSeal(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
