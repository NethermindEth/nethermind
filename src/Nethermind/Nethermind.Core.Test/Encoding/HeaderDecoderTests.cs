// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Test;
using Nethermind.Specs.Forks;
using Nethermind.Specs;
using Nethermind.Core.Specs;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class HeaderDecoderTests
{
    private const int BloomFieldIndex = 6;
    private const int MixHashFieldIndex = 13;
    private const int WithdrawalsRootFieldIndex = 16;

    [Test]
    public void Can_decode([Values] bool hasWithdrawalsRoot)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithMixHash(Keccak.Compute("mix_hash"))
            .WithNonce(1000)
            .WithWithdrawalsRoot(hasWithdrawalsRoot ? Keccak.EmptyTreeHash : null)
            .TestObject;

        HeaderDecoder decoder = new();
        byte[] rlp = decoder.Encode(header);
        RlpReader decoderContext = new(rlp);
        BlockHeader decoded = decoder.Decode(ref decoderContext)
            ?? throw new InvalidOperationException("Header decoding returned null.");
        decoded.Hash = decoded.CalculateHash();

        Assert.That(decoded.Hash, Is.EqualTo(header.Hash), "hash");
    }

    [Test]
    public void Exclude_hashes_controls_whether_the_hash_is_set([Values] bool excludeHashes)
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        HeaderDecoder decoder = new();
        RlpReader decoderContext = new(decoder.Encode(header));

        BlockHeader decoded = decoder.Decode(ref decoderContext, excludeHashes ? RlpBehaviors.ExcludeHashes : RlpBehaviors.None)!;

        Assert.That(decoded.Hash, Is.EqualTo(excludeHashes ? null : header.Hash));
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
        byte[] rlp = Rlp.Encode((BlockHeader?)null);
        BlockHeader? decoded = Rlp.Decode<BlockHeader>(rlp);
        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void Can_encode_decode_with_base_fee()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(123).TestObject;
        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);
        Assert.That(blockHeader.BaseFeePerGas, Is.EqualTo((UInt256)123));
    }

    [Test]
    public void If_baseFee_is_zero_should_not_encode()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(0).WithNonce(0).WithDifficulty(0).TestObject;
        byte[] rlp = Rlp.Encode(header);
        Assert.That(Convert.ToHexString(rlp).ToLower(), Is.EqualTo("f901f6a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2880000000000000000"));
    }

    [Test]
    public void Zero_base_fee_is_encoded_with_the_Eip1559Header_behavior()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(0).WithNonce(0).WithDifficulty(0).TestObject;
        HeaderDecoder decoder = new();
        byte[] withoutBaseFee = decoder.Encode(header);
        byte[] withBaseFee = decoder.Encode(header, RlpBehaviors.Eip1559Header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutBaseFee[..3], Is.EqualTo(Bytes.FromHexString("f901f6")));
            Assert.That(withBaseFee, Is.EqualTo((byte[])[.. Bytes.FromHexString("f901f7"), .. withoutBaseFee[3..], Rlp.EmptyByteArrayByte]));
            Assert.That(decoder.GetLength(header, RlpBehaviors.Eip1559Header), Is.EqualTo(withBaseFee.Length));
            Assert.That(header.CalculateHash(RlpBehaviors.Eip1559Header), Is.EqualTo(Keccak.Compute(withBaseFee)));
            Assert.That(DecodeHeader(withBaseFee).BaseFeePerGas, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void Can_encode_with_withdrawals()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(1).WithNonce(0).WithDifficulty(0)
            .WithWithdrawalsRoot(Keccak.Compute("withdrawals")).TestObject;
        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);
        Assert.That(blockHeader.WithdrawalsRoot, Is.EqualTo(Keccak.Compute("withdrawals")));
    }

    [Test]
    public void If_withdrawals_are_null_should_not_encode()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(1).WithNonce(0).WithDifficulty(0).TestObject;
        byte[] rlp = Rlp.Encode(header);
        Assert.That(Convert.ToHexString(rlp).ToLower(), Is.EqualTo("f901f7a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e288000000000000000001"));
    }

    [Test]
    public void Can_encode_decode_with_large_ulong_fields([Values(ulong.MaxValue, ulong.MaxValue / 2)] ulong largeValue)
    {
        BlockHeader header = Build.A.BlockHeader.
            WithNumber(largeValue).
            WithGasUsed(largeValue).
            WithGasLimit(largeValue).TestObject;

        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockHeader.GasUsed, Is.EqualTo(largeValue));
            Assert.That(blockHeader.Number, Is.EqualTo(largeValue));
            Assert.That(blockHeader.GasLimit, Is.EqualTo(largeValue));
        }
    }

    [Test]
    public void Can_encode_decode_with_large_ulong_when_using_span([Values(ulong.MaxValue, ulong.MaxValue / 2)] ulong largeValue)
    {
        BlockHeader header = Build.A.BlockHeader.
            WithNumber(largeValue).
            WithGasUsed(largeValue).
            WithGasLimit(largeValue).TestObject;

        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);

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

        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);

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

        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);

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

        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);

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

        BlockHeader decoded = DecodeHeader(Rlp.Encode(header));

        Assert.That(decoded.ExcessBlobGas, Is.Zero);
    }

    [Test]
    public void Can_encode_decode_with_missing_withdrawals_root_and_later_fields()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithBaseFee(1)
            .WithWithdrawalsRoot(null)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .TestObject;

        BlockHeader decoded = DecodeHeader(Rlp.Encode(header));

        Assert.That(decoded.WithdrawalsRoot, Is.EqualTo(Keccak.Zero));
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

        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);

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

        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);

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

    [Test]
    public void Should_reject_empty_rlp_string_for_mandatory_fixed_size_field([Values(0, 1, 2, 3, 4, 5, 6)] int fieldIndex)
    {
        byte[] validRlp = Rlp.Encode(Build.A.BlockHeader.TestObject);
        byte[] crafted = HeaderRlpTestHelper.ReplaceFieldEncoding(validRlp, fieldIndex, [0x80]);

        Assert.That(() => Rlp.Decode<BlockHeader>(crafted), Throws.InstanceOf<RlpReaderException>());
    }

    [TestCaseSource(nameof(OptionalHashRoundtripSource))]
    public void Can_encode_decode_with_null_optional_hashes_when_later_fields_are_present(BlockHeader header)
    {
        byte[] rlp = Rlp.Encode(header);
        BlockHeader blockHeader = DecodeHeader(rlp);

        Assert.That(blockHeader, Is.EqualTo(header).UsingBlockHeaderComparer());
    }

    [Test]
    public void Spec_aware_decoder_accepts_empty_rlp_string_bloom_only_when_eip7668_is_active([Values] bool eip7668)
    {
        IReleaseSpec spec = eip7668 ? new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = true } : Bogota.Instance;
        HeaderDecoder decoder = new(new TestSingleReleaseSpecProvider(spec));
        byte[] validRlp = Rlp.Encode(Build.A.BlockHeader.TestObject);
        byte[] crafted = HeaderRlpTestHelper.ReplaceFieldEncoding(validRlp, BloomFieldIndex, [0x80]);

        if (eip7668)
        {
            BlockHeader decoded = Decode(decoder);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Bloom, Is.SameAs(Bloom.ZeroLength));
                Assert.That(Rlp.Encode(decoded), Is.EqualTo(crafted));
            }
        }
        else
        {
            // Before the fork it fails exactly as the spec-less decoder does.
            string expected = Assert.Throws<RlpReaderException>(() => Decode(new HeaderDecoder()))!.Message;
            Assert.That(() => Decode(decoder), Throws.TypeOf<RlpReaderException>().With.Message.EqualTo(expected));
        }

        BlockHeader Decode(HeaderDecoder headerDecoder)
        {
            RlpReader reader = new(crafted);
            return headerDecoder.Decode(ref reader)!;
        }
    }

    [Test]
    public void Should_reject_legacy_sequence_form_bloom_in_header()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        byte[] validRlp = Rlp.Encode(header);

        byte[] listFormBloom = new byte[5 + Bloom.ByteLength];
        listFormBloom[0] = 0xF9;
        listFormBloom[1] = 0x01;
        listFormBloom[2] = 0x02;
        listFormBloom[3] = 0x81;
        listFormBloom[4] = 0x7F;
        header.Bloom!.Bytes.CopyTo(listFormBloom.AsSpan(5));

        byte[] crafted = HeaderRlpTestHelper.ReplaceFieldEncoding(validRlp, BloomFieldIndex, listFormBloom);

        Assert.That(() => Rlp.Decode<BlockHeader>(crafted), Throws.InstanceOf<RlpReaderException>());
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
        byte[] validRlp = Rlp.Encode(header);

        byte[] crafted = HeaderRlpTestHelper.ReplaceFieldEncoding(validRlp, MixHashFieldIndex, [0x80]);

        Assert.That(() => Rlp.Decode<BlockHeader>(crafted), Throws.InstanceOf<RlpReaderException>());
    }

    [Test]
    public void Should_reject_empty_rlp_string_for_withdrawals_root()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .TestObject;
        byte[] validRlp = Rlp.Encode(header);
        byte[] crafted = HeaderRlpTestHelper.ReplaceFieldEncoding(validRlp, WithdrawalsRootFieldIndex, [0x80]);

        Assert.That(() => Rlp.Decode<BlockHeader>(crafted), Throws.InstanceOf<RlpReaderException>());
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

    [Test]
    public void Can_encode_decode_requests_hash_without_parent_beacon_root()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0).TestObject;
        header.RequestsHash = TestItem.KeccakA;

        BlockHeader decoded = DecodeHeader(Rlp.Encode(header));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.ParentBeaconBlockRoot, Is.Null);
            Assert.That(decoded.RequestsHash, Is.EqualTo(TestItem.KeccakA));
        }
    }

    [Test]
    public void Can_encode_decode_slot_number_without_optional_intermediate_hashes()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0).TestObject;
        header.SlotNumber = 1;

        BlockHeader decoded = DecodeHeader(Rlp.Encode(header));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.ParentBeaconBlockRoot, Is.Null);
            Assert.That(decoded.RequestsHash, Is.Null);
            Assert.That(decoded.BlockAccessListHash, Is.Null);
            Assert.That(decoded.SlotNumber, Is.EqualTo(1));
        }
    }

    public static IEnumerable<TestCaseData> LinkageHeaderSource()
    {
        yield return new TestCaseData(Build.A.BlockHeader.WithNumber(7).WithParentHash(TestItem.KeccakC).TestObject).SetName("Legacy header");
        yield return new TestCaseData(Build.A.BlockHeader.WithNumber(ulong.MaxValue).WithBaseFee(7).TestObject).SetName("Header with base fee");
        yield return new TestCaseData(FullHeader()).SetName("Header with every optional field");
        foreach (TestCaseData data in OptionalHashRoundtripSource()) yield return data;
    }

    [TestCaseSource(nameof(LinkageHeaderSource))]
    public void Linkage_decode_keeps_the_hash_number_and_parent_hash_of_the_full_decode(BlockHeader header)
    {
        byte[] rlp = Rlp.Encode(header);
        RlpReader full = new(rlp);
        RlpReader linkage = new(rlp);

        BlockHeader expected = new HeaderDecoder().Decode(ref full)!;
        BlockHeader actual = new HeaderDecoder().DecodeLinkage(ref linkage)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Hash, Is.EqualTo(expected.Hash));
            Assert.That(actual.Number, Is.EqualTo(expected.Number));
            Assert.That(actual.ParentHash, Is.EqualTo(expected.ParentHash));
            Assert.That(linkage.BytesRead, Is.EqualTo(full.BytesRead));
        }
    }

    [Test]
    public void Linkage_decode_reads_an_empty_sequence_as_no_header()
    {
        RlpReader reader = new([0xc0]);
        Assert.That(new HeaderDecoder().DecodeLinkage(ref reader), Is.Null);
    }

    public enum LinkageDecoder { SpecLess, Eip7668Inactive, Eip7668Active }

    [Test]
    public void Linkage_decode_rejects_exactly_what_the_full_decode_rejects([Values] LinkageDecoder kind, [Values(0x00, 0x01, 0x7f, 0x80, 0x81, 0xa0, 0xb8, 0xc0, 0xf8, 0xff)] int value)
    {
        HeaderDecoder decoder = kind switch
        {
            LinkageDecoder.SpecLess => new HeaderDecoder(),
            LinkageDecoder.Eip7668Inactive => new HeaderDecoder(new TestSingleReleaseSpecProvider(Bogota.Instance)),
            _ => new HeaderDecoder(new TestSingleReleaseSpecProvider(new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = true }))
        };
        byte[] valid = Rlp.Encode(FullHeader());
        AssertLinkageDecodeMatches(decoder, valid);

        for (int position = 0; position < valid.Length; position++)
        {
            byte[] mutated = (byte[])valid.Clone();
            mutated[position] = (byte)value;
            AssertLinkageDecodeMatches(decoder, mutated);
            AssertLinkageDecodeMatches(decoder, valid.AsSpan(0, position).ToArray());
        }

        for (int field = 0; field < 21; field++)
        {
            foreach (byte[] crafted in (byte[][])[[0x80], [0x00], [0x81, 0x00], [0x82, 0x00, 0x01], [0xc0], [(byte)value], [0xa0, .. new byte[33]], [0xa1, .. new byte[33]]])
                AssertLinkageDecodeMatches(decoder, HeaderRlpTestHelper.ReplaceFieldEncoding(valid, field, crafted));
        }

        AssertLinkageDecodeMatches(decoder, AppendField(valid, [(byte)value]));
        AssertLinkageDecodeMatches(decoder, [.. valid, (byte)value]);
    }

    private static void AssertLinkageDecodeMatches(HeaderDecoder decoder, byte[] rlp)
    {
        RlpReader full = new(rlp);
        RlpReader linkage = new(rlp);
        BlockHeader? expected = null;
        BlockHeader? actual = null;
        Type? expectedError = null;
        Type? actualError = null;
        try
        {
            expected = decoder.Decode(ref full);
        }
        catch (Exception e)
        {
            expectedError = e.GetType();
        }

        try
        {
            actual = decoder.DecodeLinkage(ref linkage);
        }
        catch (Exception e)
        {
            actualError = e.GetType();
        }

        string input = rlp.ToHexString();
        Assert.That(actualError, Is.EqualTo(expectedError), input);
        if (expectedError is not null) return;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual is null, Is.EqualTo(expected is null), input);
            Assert.That(actual?.Hash, Is.EqualTo(expected?.Hash), input);
            Assert.That(actual?.Number, Is.EqualTo(expected?.Number), input);
            Assert.That(actual?.ParentHash, Is.EqualTo(expected?.ParentHash), input);
            Assert.That(linkage.BytesRead, Is.EqualTo(full.BytesRead), input);
        }
    }

    private static byte[] AppendField(byte[] headerRlp, byte[] field)
    {
        RlpReader outer = new(headerRlp);
        RlpReader reader = outer.EnterSequence(out int itemLength);
        int contentLength = reader.Remaining;
        int craftedContentLength = contentLength + field.Length;
        byte[] result = new byte[Rlp.LengthOfSequence(craftedContentLength)];
        RlpWriter writer = RlpWriter.ContentWriter(result);
        writer.StartSequence(craftedContentLength);
        int start = writer.Length;
        headerRlp.AsSpan(itemLength - contentLength, contentLength).CopyTo(result.AsSpan(start));
        field.CopyTo(result.AsSpan(start + contentLength));
        return result;
    }

    private static BlockHeader FullHeader() => Build.A.BlockHeader
        .WithNumber(1_000_000)
        .WithParentHash(TestItem.KeccakA)
        .WithDifficulty(0)
        .WithExtraData([1, 2, 3])
        .WithTimestamp(1_700_000_000)
        .WithBaseFee(7)
        .WithWithdrawalsRoot(TestItem.KeccakB)
        .WithBlobGasUsed(131072)
        .WithExcessBlobGas(0)
        .WithParentBeaconBlockRoot(TestItem.KeccakC)
        .WithRequestsHash(TestItem.KeccakD)
        .WithBlockAccessListHash(TestItem.KeccakE)
        .WithSlotNumber(42)
        .TestObject;

    private static BlockHeader DecodeHeader(byte[] rlp) =>
        Rlp.Decode<BlockHeader>(rlp) ?? throw new InvalidOperationException("Header decoding returned null.");

}

public static class HeaderRlpTestHelper
{
    public static byte[] ReplaceFieldEncoding(byte[] headerRlp, int fieldIndex, byte[] craftedField)
    {
        RlpReader outer = new(headerRlp);
        RlpReader reader = outer.EnterSequence(out int itemLength);
        int contentLength = reader.Remaining;
        int prefixLength = itemLength - contentLength;

        int fieldStart = prefixLength;
        int fieldLength = 0;
        for (int i = 0; i <= fieldIndex; i++)
        {
            fieldStart = prefixLength + reader.BytesRead;
            fieldLength = reader.ReadEncodedItem().Length;
        }

        int contentLengthDelta = craftedField.Length - fieldLength;
        int craftedContentLength = contentLength + contentLengthDelta;
        byte[] result = new byte[Rlp.LengthOfSequence(craftedContentLength)];
        RlpWriter writer = RlpWriter.ContentWriter(result);
        writer.StartSequence(craftedContentLength);

        int craftedContentStart = writer.Length;
        headerRlp.AsSpan(prefixLength, fieldStart - prefixLength).CopyTo(result.AsSpan(craftedContentStart));
        craftedField.CopyTo(result.AsSpan(craftedContentStart + fieldStart - prefixLength));
        headerRlp.AsSpan(fieldStart + fieldLength, prefixLength + contentLength - fieldStart - fieldLength)
            .CopyTo(result.AsSpan(craftedContentStart + fieldStart - prefixLength + craftedField.Length));

        return result;
    }
}
