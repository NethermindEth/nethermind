// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class HeaderDecoderTests
{
    private const int BloomFieldIndex = 6;
    private const int MixHashFieldIndex = 13;
    private const int WithdrawalsRootFieldIndex = 16;

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
        RlpReader decoderContext = new(rlp.Bytes);
        BlockHeader? decoded = decoder.Decode(ref decoderContext);
        decoded!.Hash = decoded.CalculateHash();

        Assert.That(decoded.Hash, Is.EqualTo(header.Hash), "hash");
    }

    [Test]
    public void Get_length_null()
    {
        HeaderDecoder decoder = new();
        Assert.That(decoder.GetLength((BlockHeader?)null, RlpBehaviors.None), Is.EqualTo(1));
    }

    [Test]
    public void Can_handle_nulls()
    {
        Rlp rlp = Rlp.Encode((BlockHeader?)null);
        BlockHeader decoded = Rlp.Decode<BlockHeader>(rlp);
        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void Can_encode_decode_with_base_fee()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(123).TestObject;
        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);
        Assert.That(blockHeader.BaseFeePerGas, Is.EqualTo((UInt256)123));
    }

    [Test]
    public void If_baseFee_is_zero_should_not_encode()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(0).WithNonce(0).WithDifficulty(0).TestObject;
        Rlp rlp = Rlp.Encode(header);
        Assert.That(Convert.ToHexString(rlp.Bytes).ToLower(), Is.EqualTo("f901f6a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2880000000000000000"));
    }

    [Test]
    public void Can_encode_with_withdrawals()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(1).WithNonce(0).WithDifficulty(0)
            .WithWithdrawalsRoot(Keccak.Compute("withdrawals")).TestObject;
        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);
        Assert.That(blockHeader.WithdrawalsRoot, Is.EqualTo(Keccak.Compute("withdrawals")));
    }

    [Test]
    public void If_withdrawals_are_null_should_not_encode()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(1).WithNonce(0).WithDifficulty(0).TestObject;
        Rlp rlp = Rlp.Encode(header);
        Assert.That(Convert.ToHexString(rlp.Bytes).ToLower(), Is.EqualTo("f901f7a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e288000000000000000001"));
    }

    [TestCase(ulong.MaxValue)]
    [TestCase(ulong.MaxValue / 2)]
    public void Can_encode_decode_with_large_ulong_fields(ulong largeValue)
    {
        BlockHeader header = Build.A.BlockHeader.
            WithNumber(largeValue).
            WithGasUsed(largeValue).
            WithGasLimit(largeValue).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockHeader.GasUsed, Is.EqualTo(largeValue));
            Assert.That(blockHeader.Number, Is.EqualTo(largeValue));
            Assert.That(blockHeader.GasLimit, Is.EqualTo(largeValue));
        }
    }

    [TestCase(ulong.MaxValue)]
    [TestCase(ulong.MaxValue / 2)]
    public void Can_encode_decode_with_large_ulong_when_using_span(ulong largeValue)
    {
        BlockHeader header = Build.A.BlockHeader.
            WithNumber(largeValue).
            WithGasUsed(largeValue).
            WithGasLimit(largeValue).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockHeader.GasUsed, Is.EqualTo(largeValue));
            Assert.That(blockHeader.Number, Is.EqualTo(largeValue));
            Assert.That(blockHeader.GasLimit, Is.EqualTo(largeValue));
        }
    }

    [TestCaseSource(nameof(CancunFieldsSource))]
    public void Can_encode_decode_with_cancun_fields(ulong? blobGasUsed, ulong? excessBlobGas, Hash256? parentBeaconBlockRoot)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(blobGasUsed)
            .WithExcessBlobGas(excessBlobGas)
            .WithParentBeaconBlockRoot(parentBeaconBlockRoot).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockHeader.BlobGasUsed, Is.EqualTo(blobGasUsed));
            Assert.That(blockHeader.ExcessBlobGas, Is.EqualTo(excessBlobGas));
        }
    }

    [Test]
    public void Can_encode_decode_with_WithdrawalRequestRoot()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakB)
            .WithRequestsHash(Keccak.Zero).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        Assert.That(blockHeader.ParentBeaconBlockRoot, Is.EqualTo(TestItem.KeccakB));
    }

    [Test]
    public void Can_encode_decode_with_ValidatorExitRoot_equals_to_null()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakB)
            .WithRequestsHash(Keccak.Zero).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        Assert.That(blockHeader, Is.EqualTo(header).UsingBlockHeaderComparer());
    }

    [Test]
    public void Can_encode_decode_with_missing_excess_blob_gas()
    {
        BlockHeader header = Build.A.BlockHeader
                .WithHash(new Hash256("0x3d8b9cc98eee58243461bd5a83663384b50293cd1e459a6841cb005296305590"))
                .WithNumber(1000)
                .WithParentHash(new Hash256("0x793b1ee71748f4b1b70cf70a53e083e6d5d356bffee9946e15a13fed8d70d7d6"))
                .WithBeneficiary(new Address("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099"))
                .WithGasLimit(100000000)
                .WithGasUsed(299331)
                .WithTimestamp(1736575828)
                .WithExtraData(Bytes.FromHexString("4e65746865726d696e64"))
                .WithDifficulty(1)
                .WithMixHash(new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000"))
                .WithNonce(0)
                .WithUnclesHash(new Hash256("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
                .WithTransactionsRoot(new Hash256("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
                .WithReceiptsRoot(new Hash256("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
                .WithStateRoot(new Hash256("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
                .WithBaseFee(8)
                .WithWithdrawalsRoot(new Hash256("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"))
                .WithBlobGasUsed(0)
                .TestObject;
        ;

        Rlp rlp = Rlp.Encode(header);
        _ = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());
    }

    [Test]
    public void Can_encode_decode_with_zero_basefee_but_has_later_field()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(0)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakB)
            .WithRequestsHash(Keccak.Zero).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        Assert.That(blockHeader, Is.EqualTo(header).UsingBlockHeaderComparer());
    }

    [Test]
    public void Can_encode_decode_with_null_mandatory_fields()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        header.ParentHash = null;
        header.UnclesHash = null;
        header.Beneficiary = null;
        header.StateRoot = null;
        header.TxRoot = null;
        header.ReceiptsRoot = null;
        header.Bloom = null;
        header.MixHash = null;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockHeader.ParentHash, Is.EqualTo(Keccak.Zero));
            Assert.That(blockHeader.UnclesHash, Is.EqualTo(Keccak.OfAnEmptySequenceRlp));
            Assert.That(blockHeader.Beneficiary, Is.EqualTo(Address.Zero));
            Assert.That(blockHeader.StateRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(blockHeader.TxRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(blockHeader.ReceiptsRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(blockHeader.Bloom, Is.EqualTo(Bloom.Empty));
            Assert.That(blockHeader.MixHash, Is.EqualTo(Keccak.Zero));
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public void Should_reject_empty_rlp_string_for_mandatory_fixed_size_field(int fieldIndex)
    {
        byte[] validRlp = Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes;
        byte[] crafted = HeaderRlpTestHelper.ReplaceFieldEncoding(validRlp, fieldIndex, [0x80]);

        Assert.That(() => Rlp.Decode<BlockHeader>(crafted), Throws.InstanceOf<RlpException>());
    }

    [TestCaseSource(nameof(OptionalHashRoundtripSource))]
    public void Can_encode_decode_with_null_optional_hashes_when_later_fields_are_present(BlockHeader header)
    {
        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        Assert.That(blockHeader, Is.EqualTo(header).UsingBlockHeaderComparer());
    }

    [Test]
    public void Should_reject_legacy_sequence_form_bloom_in_header()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        byte[] validRlp = Rlp.Encode(header).Bytes;

        byte[] listFormBloom = new byte[5 + Bloom.ByteLength];
        listFormBloom[0] = 0xF9;
        listFormBloom[1] = 0x01;
        listFormBloom[2] = 0x02;
        listFormBloom[3] = 0x81;
        listFormBloom[4] = 0x7F;
        header.Bloom!.Bytes.CopyTo(listFormBloom.AsSpan(5));

        byte[] crafted = HeaderRlpTestHelper.ReplaceFieldEncoding(validRlp, BloomFieldIndex, listFormBloom);

        Assert.That(() => Rlp.Decode<BlockHeader>(crafted), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Should_reject_empty_rlp_string_for_prev_randao()
    {
        Hash256 mixHash = Keccak.Compute("mix_hash");
        BlockHeader header = Build.A.BlockHeader
            .WithMixHash(mixHash)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .TestObject;
        byte[] validRlp = Rlp.Encode(header).Bytes;

        byte[] crafted = HeaderRlpTestHelper.ReplaceFieldEncoding(validRlp, MixHashFieldIndex, [0x80]);

        Assert.That(() => Rlp.Decode<BlockHeader>(crafted), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Should_reject_empty_rlp_string_for_withdrawals_root()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .TestObject;
        byte[] validRlp = Rlp.Encode(header).Bytes;
        byte[] crafted = HeaderRlpTestHelper.ReplaceFieldEncoding(validRlp, WithdrawalsRootFieldIndex, [0x80]);

        Assert.That(() => Rlp.Decode<BlockHeader>(crafted), Throws.InstanceOf<RlpException>());
    }

    public static IEnumerable<TestCaseData> OptionalHashRoundtripSource()
    {
        yield return new TestCaseData(Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(null)
            .WithRequestsHash(TestItem.KeccakA)
            .TestObject).SetName("Null parent beacon block root with later field");

        yield return new TestCaseData(Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakA)
            .WithRequestsHash(null)
            .WithBlockAccessListHash(TestItem.KeccakB)
            .TestObject).SetName("Null requests hash with later field");

        yield return new TestCaseData(Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakA)
            .WithRequestsHash(TestItem.KeccakB)
            .WithBlockAccessListHash(null)
            .WithSlotNumber(1)
            .TestObject).SetName("Null block access list hash with later field");
    }

    public static IEnumerable<object?[]> CancunFieldsSource()
    {
        yield return new object?[] { null, null, null };
        yield return new object?[] { 0ul, 0ul, TestItem.KeccakA };
        yield return new object?[] { 1ul, 2ul, TestItem.KeccakB };
        yield return new object?[] { ulong.MaxValue / 2, ulong.MaxValue, null };
        yield return new object?[] { ulong.MaxValue, ulong.MaxValue / 2, null };
    }
}

public static class HeaderRlpTestHelper
{
    public static byte[] ReplaceFieldEncoding(byte[] headerRlp, int fieldIndex, byte[] craftedField)
    {
        RlpReader reader = new(headerRlp);
        (int prefixLength, int contentLength) = reader.ReadPrefixAndContentLength();

        int fieldStart = reader.Position;
        int fieldLength = 0;
        for (int i = 0; i <= fieldIndex; i++)
        {
            fieldStart = reader.Position;
            fieldLength = reader.PeekNextRlpLength();
            reader.SkipItem();
        }

        int contentLengthDelta = craftedField.Length - fieldLength;
        int craftedContentLength = contentLength + contentLengthDelta;
        byte[] result = new byte[Rlp.LengthOfSequence(craftedContentLength)];
        RlpWriter writer = new(result);
        writer.StartSequence(craftedContentLength);

        int craftedContentStart = writer.Position;
        headerRlp.AsSpan(prefixLength, fieldStart - prefixLength).CopyTo(result.AsSpan(craftedContentStart));
        craftedField.CopyTo(result.AsSpan(craftedContentStart + fieldStart - prefixLength));
        headerRlp.AsSpan(fieldStart + fieldLength, prefixLength + contentLength - fieldStart - fieldLength)
            .CopyTo(result.AsSpan(craftedContentStart + fieldStart - prefixLength + craftedField.Length));

        return result;
    }
}
