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

public class BlockDecoderTests
{
    private readonly Block[] _scenarios;

    public BlockDecoderTests()
    {
        var transactions = new Transaction[100];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithData(new byte[] { (byte)i })
                .WithNonce((UInt256)i)
                .WithValue((UInt256)i)
                .Signed(new EthereumEcdsa(TestBlockchainIds.ChainId), TestItem.PrivateKeyA, true)
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
                .TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithBaseFeePerGas(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithBlobGasUsed(0)
                .WithExcessBlobGas(0)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithBaseFeePerGas(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithBlobGasUsed(0xff)
                .WithExcessBlobGas(0xff)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithBaseFeePerGas(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithBlobGasUsed(ulong.MaxValue)
                .WithExcessBlobGas(ulong.MaxValue)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            Build.A.Block.WithNumber(1)
                .WithBaseFeePerGas(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithBlobGasUsed(ulong.MaxValue)
                .WithExcessBlobGas(ulong.MaxValue)
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
        Assert.That(decoded, Is.Null);
    }

    private readonly string regression5644 = "f902cff9025aa05297f2a4a699ba7d038a229a8eb7ab29d0073b37376ff0311f2bd9c608411830a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0fe77dd4ad7c2a3fa4c11868a00e4d728adcdfef8d2e3c13b256b06cbdbb02ec9a00d0abe08c162e4e0891e7a45a8107a98ae44ed47195c2d041fe574de40272df0a0056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000182160c837a1200825208845c54648eb8613078366336393733363936653733366236390000000000000000000000000000f3ec96e458292ccea72a1e53e95f94c28051ab51880b7e03d933f7fa78c9692f635ae55ac3899c9c6999d33c758b5248a05894a3471282333bcd76067c5d391300a00000000000000000000000000000000000000000000000000000000000000000880000000000000000f86ff86d80843b9aca008252089422ea9f6b28db76a7162054c05ed812deb2f519cd8a152d02c7e14af6800000802da0f67424c67d9f91a87b5437db1bdaa05e29bd020ab474b2f67f7be163c9f650dda02f90ab34b44165d776ae04449b15210076d6a72abe2bda2903d4b87f0d1ce541c0";

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

    [TestCase("0xf902cef9025ba055870e2f3ef77a9e6163ee5c005dc51d648a2eead382b9044b1a5ad2ee69b0c6a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347942adc25665018aa1fe0e6bc666dac8fc2697ff9baa0b77e3b74c6c8af85408677375183385a2e55446bd071bf193a4958f7417dc8fba056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000800188016345785d8a0000800c80a0000000000000000000000000000000000000000000000000000000000000000088000000000000000007a0cc3b10b54dc4e97c01f1df20e8b95874cd5fe83bf6eae64935a16cb08db85fa98080a00000000000000000000000000000000000000000000000000000000000000000a0e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855c0c0f86ce08080946389e7f33ce3b1e94e4325ef02829cd12297ef7188ffffffffffffffffd80180948a0a19589531694250d570040a0c4b74576919b801d8028094000000000000000000000000000000000000100080d8038094a94f5374fce5edbc8e2a8697c15331677e6ebf0b80")]
    [Ignore("The test is useful for debugging hive - shouldn't be executed on CI")]
    public void Write_rlp_of_blocks_to_file(string rlp)
    {
        // the test is useful for debugging hive
        File.WriteAllBytes("chains\\block1.rlp".GetApplicationResourcePath(), Bytes.FromHexString(rlp));
    }

    [Test]
    public void Get_length_null()
    {
        BlockDecoder decoder = new();
        Assert.That(decoder.GetLength(null, RlpBehaviors.None), Is.EqualTo(1));
    }
}
