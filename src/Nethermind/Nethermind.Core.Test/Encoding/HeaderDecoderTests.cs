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

    [TestCase(0UL)]
    [TestCase(100000000UL)]
    public void Can_decode_aura(ulong step)
    {
        byte[] auRaSignature = new byte[64];
        new Random().NextBytes(auRaSignature);
        BlockHeader header = Build.A.BlockHeader
            .WithAura(step, auRaSignature)
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
        BlockHeader? decoded = Rlp.Decode<BlockHeader>(rlp);
        Assert.That(decoded, Is.Null);
    }

    [TestCase(nameof(BlockHeader.ParentHash))]
    [TestCase(nameof(BlockHeader.UnclesHash))]
    [TestCase(nameof(BlockHeader.Beneficiary))]
    [TestCase(nameof(BlockHeader.StateRoot))]
    [TestCase(nameof(BlockHeader.TxRoot))]
    [TestCase(nameof(BlockHeader.ReceiptsRoot))]
    [TestCase(nameof(BlockHeader.Bloom))]
    public void Decode_throws_on_null_required_field(string fieldName)
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        switch (fieldName)
        {
            case nameof(BlockHeader.ParentHash):
                header.ParentHash = null;
                break;
            case nameof(BlockHeader.UnclesHash):
                header.UnclesHash = null;
                break;
            case nameof(BlockHeader.Beneficiary):
                header.Beneficiary = null;
                break;
            case nameof(BlockHeader.StateRoot):
                header.StateRoot = null;
                break;
            case nameof(BlockHeader.TxRoot):
                header.TxRoot = null;
                break;
            case nameof(BlockHeader.ReceiptsRoot):
                header.ReceiptsRoot = null;
                break;
            case nameof(BlockHeader.Bloom):
                header.Bloom = null;
                break;
        }

        HeaderDecoder decoder = new();
        Rlp rlp = decoder.Encode(header);

        Assert.That(Decode, Throws.TypeOf<RlpException>());

        void Decode()
        {
            RlpReader reader = new(rlp.Bytes);
            decoder.Decode(ref reader);
        }
    }

    [TestCase(nameof(BlockHeader.WithdrawalsRoot))]
    [TestCase(nameof(BlockHeader.ParentBeaconBlockRoot))]
    [TestCase(nameof(BlockHeader.RequestsHash))]
    [TestCase(nameof(BlockHeader.BlockAccessListHash))]
    public void Decode_throws_on_null_present_fork_hash_field(string fieldName)
    {
        byte[] bytes = EncodeHeaderWithNullPresentForkHashField(fieldName);
        HeaderDecoder decoder = new();

        Assert.That(Decode, Throws.TypeOf<RlpException>());

        void Decode()
        {
            RlpReader reader = new(bytes);
            decoder.Decode(ref reader);
        }
    }

    [Test]
    public void Decode_throws_on_null_mix_hash()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        header.MixHash = null;
        HeaderDecoder decoder = new();
        Rlp rlp = decoder.Encode(header);

        Assert.That(Decode, Throws.TypeOf<RlpException>());

        void Decode()
        {
            RlpReader reader = new(rlp.Bytes);
            decoder.Decode(ref reader);
        }
    }

    [Test]
    public void Can_encode_decode_with_base_fee()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(123).TestObject;
        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp)!;
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
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp)!;
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
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp)!;

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
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan())!;

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
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan())!;

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
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan())!;

        Assert.That(blockHeader.ParentBeaconBlockRoot, Is.EqualTo(TestItem.KeccakB));
    }

    [Test]
    public void Can_encode_decode_with_requests_hash_and_missing_parent_beacon_root()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithRequestsHash(TestItem.KeccakA).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan())!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockHeader.ParentBeaconBlockRoot, Is.EqualTo(Keccak.Zero));
            Assert.That(blockHeader.RequestsHash, Is.EqualTo(TestItem.KeccakA));
        }
    }

    [Test]
    public void Can_encode_decode_with_slot_number_and_missing_intermediate_hashes()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithSlotNumber(1).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan())!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockHeader.ParentBeaconBlockRoot, Is.EqualTo(Keccak.Zero));
            Assert.That(blockHeader.RequestsHash, Is.EqualTo(Keccak.Zero));
            Assert.That(blockHeader.BlockAccessListHash, Is.EqualTo(Keccak.Zero));
            Assert.That(blockHeader.SlotNumber, Is.EqualTo(1));
        }
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
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan())!;

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
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan())!;

        Assert.That(blockHeader, Is.EqualTo(header).UsingBlockHeaderComparer());
    }

    public static IEnumerable<object?[]> CancunFieldsSource()
    {
        yield return new object?[] { null, null, null };
        yield return new object?[] { 0ul, 0ul, TestItem.KeccakA };
        yield return new object?[] { 1ul, 2ul, TestItem.KeccakB };
        yield return new object?[] { ulong.MaxValue / 2, ulong.MaxValue, null };
        yield return new object?[] { ulong.MaxValue, ulong.MaxValue / 2, null };
    }

    private static byte[] EncodeHeaderWithNullPresentForkHashField(string fieldName)
    {
        bool includeBlobFields = fieldName is nameof(BlockHeader.ParentBeaconBlockRoot) or nameof(BlockHeader.RequestsHash) or nameof(BlockHeader.BlockAccessListHash);
        bool includeRequestsHash = fieldName is nameof(BlockHeader.RequestsHash) or nameof(BlockHeader.BlockAccessListHash);
        bool includeBlockAccessListHash = fieldName is nameof(BlockHeader.BlockAccessListHash);

        UInt256 difficulty = 1;
        UInt256 baseFee = 1;
        Hash256? withdrawalsRoot = fieldName == nameof(BlockHeader.WithdrawalsRoot) ? null : Keccak.Zero;
        Hash256? parentBeaconBlockRoot = fieldName == nameof(BlockHeader.ParentBeaconBlockRoot) ? null : Keccak.Zero;
        Hash256? requestsHash = fieldName == nameof(BlockHeader.RequestsHash) ? null : Keccak.Zero;
        Hash256? blockAccessListHash = null;

        int contentLength = Rlp.LengthOf(TestItem.KeccakA)
            + Rlp.LengthOf(TestItem.KeccakB)
            + Rlp.LengthOf(TestItem.AddressA)
            + Rlp.LengthOf(TestItem.KeccakC)
            + Rlp.LengthOf(TestItem.KeccakD)
            + Rlp.LengthOf(TestItem.KeccakE)
            + Rlp.LengthOf(Bloom.Empty)
            + Rlp.LengthOf(difficulty)
            + Rlp.LengthOf(1UL)
            + Rlp.LengthOf(1UL)
            + Rlp.LengthOf(0UL)
            + Rlp.LengthOf(1UL)
            + Rlp.LengthOf(Array.Empty<byte>())
            + Rlp.LengthOf(Keccak.Zero)
            + Rlp.LengthOfNonce(0UL)
            + Rlp.LengthOf(baseFee)
            + Rlp.LengthOf(withdrawalsRoot);

        if (includeBlobFields)
        {
            contentLength += Rlp.LengthOf(0UL);
            contentLength += Rlp.LengthOf(0UL);
            contentLength += Rlp.LengthOf(parentBeaconBlockRoot);
        }

        if (includeRequestsHash)
        {
            contentLength += Rlp.LengthOf(requestsHash);
        }

        if (includeBlockAccessListHash)
        {
            contentLength += Rlp.LengthOf(blockAccessListHash);
        }

        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        writer.Encode(TestItem.KeccakA);
        writer.Encode(TestItem.KeccakB);
        writer.Encode(TestItem.AddressA);
        writer.Encode(TestItem.KeccakC);
        writer.Encode(TestItem.KeccakD);
        writer.Encode(TestItem.KeccakE);
        writer.Encode(Bloom.Empty);
        writer.Encode(difficulty);
        writer.Encode(1UL);
        writer.Encode(1UL);
        writer.Encode(0UL);
        writer.Encode(1UL);
        writer.Encode(Array.Empty<byte>());
        writer.Encode(Keccak.Zero);
        writer.Encode((UInt256)0, HeaderDecoder.NonceLength);
        writer.Encode(baseFee);
        writer.Encode(withdrawalsRoot);

        if (includeBlobFields)
        {
            writer.Encode(0UL);
            writer.Encode(0UL);
            writer.Encode(parentBeaconBlockRoot);
        }

        if (includeRequestsHash)
        {
            writer.Encode(requestsHash);
        }

        if (includeBlockAccessListHash)
        {
            writer.Encode(blockAccessListHash);
        }

        return bytes;
    }
}
