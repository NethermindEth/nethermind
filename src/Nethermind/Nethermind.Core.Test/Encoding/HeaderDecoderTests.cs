// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public class HeaderDecoderTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void Can_decode(bool hasWithdrawalsRoot)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithMixHash(Keccak.Compute("mix_hash"))
            .WithNonce(1000)
            .WithWithdrawalsRoot(hasWithdrawalsRoot ? Keccak.EmptyTreeHash : null)
            .TestObject;

        HeaderDecoder decoder = new();
        Rlp rlp = decoder.Encode(header);
        Rlp.ValueDecoderContext decoderContext = new(rlp.Bytes);
        BlockHeader? decoded = decoder.Decode(ref decoderContext);
        decoded!.Hash = decoded.CalculateHash();

        Assert.That(decoded.Hash, Is.EqualTo(header.Hash), "hash");
    }

    [Test]
    public void Can_decode_tricky()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithMixHash(Keccak.Compute("mix_hash"))
            .WithTimestamp(2730)
            .WithNonce(1000)
            .TestObject;

        HeaderDecoder decoder = new();
        Rlp rlp = decoder.Encode(header);
        rlp.Bytes[2]++;
        string bytesWithAAA = rlp.Bytes.ToHexString();
        bytesWithAAA = bytesWithAAA.Replace("820aaa", "83000aaa");

        rlp = new Rlp(Bytes.FromHexString(bytesWithAAA));

        Rlp.ValueDecoderContext decoderContext = new(rlp.Bytes);
        BlockHeader? decoded = decoder.Decode(ref decoderContext);
        decoded!.Hash = decoded.CalculateHash();

        Assert.That(decoded.Hash, Is.EqualTo(header.Hash), "hash");
    }

    [Test]
    public void Can_decode_aura()
    {
        var auRaSignature = new byte[64];
        new Random().NextBytes(auRaSignature);
        BlockHeader header = Build.A.BlockHeader
            .WithAura(100000000, auRaSignature)
            .TestObject;

        HeaderDecoder decoder = new();
        Rlp rlp = decoder.Encode(header);
        Rlp.ValueDecoderContext decoderContext = new(rlp.Bytes);
        BlockHeader? decoded = decoder.Decode(ref decoderContext);
        decoded!.Hash = decoded.CalculateHash();

        Assert.That(decoded.Hash, Is.EqualTo(header.Hash), "hash");
    }

    [Test]
    public void Get_length_null()
    {
        HeaderDecoder decoder = new();
        Assert.That(decoder.GetLength(null, RlpBehaviors.None), Is.EqualTo(1));
    }

    [Test]
    public void Can_handle_nulls()
    {
        Rlp rlp = Rlp.Encode((BlockHeader?)null);
        BlockHeader decoded = Rlp.Decode<BlockHeader>(rlp);
        Assert.Null(decoded);
    }

    [Test]
    public void Can_encode_decode_with_base_fee()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(123).TestObject;
        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);
        blockHeader.BaseFeePerGas.Should().Be((UInt256)123);
    }

    [Test]
    public void If_baseFee_is_zero_should_not_encode()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(0).WithNonce(0).WithDifficulty(0).TestObject;
        Rlp rlp = Rlp.Encode(header);
        Convert.ToHexString(rlp.Bytes).ToLower().Should().Be("f901f6a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2880000000000000000");
    }

    [Test]
    public void Can_encode_with_withdrawals()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(1).WithNonce(0).WithDifficulty(0)
            .WithWithdrawalsRoot(Keccak.Compute("withdrawals")).TestObject;
        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);
        blockHeader.WithdrawalsRoot.Should().Be(Keccak.Compute("withdrawals"));
    }

    [Test]
    public void If_withdrawals_are_null_should_not_encode()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(1).WithNonce(0).WithDifficulty(0).TestObject;
        Rlp rlp = Rlp.Encode(header);
        Convert.ToHexString(rlp.Bytes).ToLower().Should().Be("f901f7a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e288000000000000000001");
    }

    [TestCaseSource(nameof(ExcessDataGasCaseSource))]
    public void Can_encode_decode_with_excessDataGas(ulong? dataGasUsed, ulong? excessDataGas)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithDataGasUsed(dataGasUsed)
            .WithExcessDataGas(excessDataGas).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        blockHeader.DataGasUsed.Should().Be(dataGasUsed);
        blockHeader.ExcessDataGas.Should().Be(excessDataGas);
    }

    public static IEnumerable<object?[]> ExcessDataGasCaseSource()
    {
        yield return new object[] { null, null };
        yield return new object[] { 0ul, 0ul };
        yield return new object[] { 1ul, 2ul };
        yield return new object[] { ulong.MaxValue / 2, ulong.MaxValue };
        yield return new object[] { ulong.MaxValue, ulong.MaxValue / 2 };
    }

    [TestCase(-1)]
    [TestCase(long.MinValue)]
    public void Can_encode_decode_with_negative_long_fields(long negativeLong)
    {
        BlockHeader header = Build.A.BlockHeader.
            WithNumber(negativeLong).
            WithGasUsed(negativeLong).
            WithGasLimit(negativeLong).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);

        blockHeader.GasUsed.Should().Be(negativeLong);
        blockHeader.Number.Should().Be(negativeLong);
        blockHeader.GasLimit.Should().Be(negativeLong);
    }

    [TestCase(-1)]
    [TestCase(long.MinValue)]
    public void Can_encode_decode_with_negative_long_when_using_span(long negativeLong)
    {
        BlockHeader header = Build.A.BlockHeader.
            WithNumber(negativeLong).
            WithGasUsed(negativeLong).
            WithGasLimit(negativeLong).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        blockHeader.GasUsed.Should().Be(negativeLong);
        blockHeader.Number.Should().Be(negativeLong);
        blockHeader.GasLimit.Should().Be(negativeLong);
    }
}
