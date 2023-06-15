// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public class BlockDecoderTests
{
    private Block[] _scenarios;

    public BlockDecoderTests()
    {
        var transactions = new Transaction[100];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithData(new byte[] { (byte)i })
                .WithNonce((UInt256)i)
                .WithValue((UInt256)i)
                .Signed(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance), TestItem.PrivateKeyA, true)
                .TestObject;
        }

        var uncles = new BlockHeader[16];

        for (var i = 0; i < uncles.Length; i++)
        {
            uncles[i] = Build.A.BlockHeader
                .WithWithdrawalsRoot(i % 3 == 0 ? null : Keccak.Compute(i.ToString()))
                .TestObject;
        }

        _scenarios = new[]
        {
            Build.A.Block.WithNumber(1).TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithTransactions(transactions)
                .WithUncles(Build.A.BlockHeader.TestObject)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject
        };
    }

    [Test]
    public void Can_do_roundtrip_null([Values(true, false)] bool valueDecoder)
    {
        BlockDecoder decoder = new();
        Rlp result = decoder.Encode(null);
        Block decoded = valueDecoder ? Rlp.Decode<Block>(result.Bytes.AsSpan()) : Rlp.Decode<Block>(result);
        Assert.IsNull(decoded);
    }

    private string regression5644 = "f902cff9025aa05297f2a4a699ba7d038a229a8eb7ab29d0073b37376ff0311f2bd9c608411830a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0fe77dd4ad7c2a3fa4c11868a00e4d728adcdfef8d2e3c13b256b06cbdbb02ec9a00d0abe08c162e4e0891e7a45a8107a98ae44ed47195c2d041fe574de40272df0a0056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000182160c837a1200825208845c54648eb8613078366336393733363936653733366236390000000000000000000000000000f3ec96e458292ccea72a1e53e95f94c28051ab51880b7e03d933f7fa78c9692f635ae55ac3899c9c6999d33c758b5248a05894a3471282333bcd76067c5d391300a00000000000000000000000000000000000000000000000000000000000000000880000000000000000f86ff86d80843b9aca008252089422ea9f6b28db76a7162054c05ed812deb2f519cd8a152d02c7e14af6800000802da0f67424c67d9f91a87b5437db1bdaa05e29bd020ab474b2f67f7be163c9f650dda02f90ab34b44165d776ae04449b15210076d6a72abe2bda2903d4b87f0d1ce541c0";

    [Test]
    public void Can_do_roundtrip_regression([Values(true, false)] bool valueDecoder)
    {
        BlockDecoder decoder = new();

        byte[] bytes = Bytes.FromHexString(regression5644);
        Rlp.ValueDecoderContext valueDecoderContext = new(bytes);
        Block? decoded = valueDecoder ? decoder.Decode(ref valueDecoderContext) : decoder.Decode(new RlpStream(bytes));
        Rlp encoded = decoder.Encode(decoded);
        Assert.That(encoded.Bytes.ToHexString(), Is.EqualTo(bytes.ToHexString()));
    }

    [Test]
    public void Can_do_roundtrip_scenarios([Values(true, false)] bool valueDecoder)
    {
        BlockDecoder decoder = new();
        foreach (Block block in _scenarios)
        {
            Rlp encoded = decoder.Encode(block);
            Rlp.ValueDecoderContext valueDecoderContext = new(encoded.Bytes);
            Block? decoded = valueDecoder ? decoder.Decode(ref valueDecoderContext) : decoder.Decode(new RlpStream(encoded.Bytes));
            Rlp encoded2 = decoder.Encode(decoded);
            Assert.That(encoded2.Bytes.ToHexString(), Is.EqualTo(encoded.Bytes.ToHexString()));
        }
    }

    [TestCase("0xf90265f901fda00a7e4c1b7404e89fd6b1bc19148594f98b472a87ca152938b242343296da619da01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347942adc25665018aa1fe0e6bc666dac8fc2697ff9baa01ac2883e8f3f17f58488c6933524298dec316fd596614832065748274a336391a07e2d13609f335a7caf015192b353ce5abec6d37a00726d862b9d287a98addb51a0a0e10907f175886de9bd8cd4ac2c21d1db4109a3a9fecf60f54015ee102803fdb90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008302000001887fffffffffffffff830249f08203e800a00000000000000000000000000000000000000000000000000000000000000000880000000000000000f862f860800a830249f094100000000000000000000000000000000000000001801ba0f56f3b98c5ed3c38d0e4e1e3e499b6ba9bda60fcf0f6a811d7979fb5d81cec53a00be599284605e5223d1fc0a043f56e1a6a9ec2802406f664cbfea850323aeabfc0")]
    [Ignore("The test is useful for debugging hive - shouldn't be executed on CI")]
    public void Write_rlp_of_blocks_to_file(string rlp)
    {
        // the test is useful for debugging hive
        File.WriteAllBytes("C:\\blocks\\00001.rlp", Bytes.FromHexString(rlp));
    }

    [Test]
    public void Get_length_null()
    {
        BlockDecoder decoder = new();
        Assert.That(decoder.GetLength(null, RlpBehaviors.None), Is.EqualTo(1));
    }
}
