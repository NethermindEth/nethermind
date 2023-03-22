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

        Assert.AreEqual(header.Hash, decoded.Hash, "hash");
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

        Assert.AreEqual(header.Hash, decoded.Hash, "hash");
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

        Assert.AreEqual(header.Hash, decoded.Hash, "hash");
    }

    [Test]
    public void Get_length_null()
    {
        HeaderDecoder decoder = new();
        Assert.AreEqual(1, decoder.GetLength(null, RlpBehaviors.None));
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

    [TestCaseSource(nameof(ExcessDataGasCaseSource))]
    public void Can_encode_decode_with_excessDataGas(UInt256? excessDataGas)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithExcessDataGas(excessDataGas).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        blockHeader.ExcessDataGas.Should().Be(excessDataGas);
    }

    public static IEnumerable<UInt256?> ExcessDataGasCaseSource()
    {
        yield return null;
        yield return UInt256.Zero;
        yield return new UInt256(1);
        yield return UInt256.UInt128MaxValue;
        yield return UInt256.MaxValue;
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
