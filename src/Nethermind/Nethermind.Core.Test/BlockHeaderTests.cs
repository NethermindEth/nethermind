// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[TestFixture]
public class BlockHeaderTests
{
    [Test]
    public void Hash_as_expected()
    {
        BlockHeader header = new()
        {
            Bloom = new Bloom(Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")),
            Beneficiary = new Address("0x8888f1f195afa192cfee860698584c030f4c9db1"),
            Difficulty = Bytes.FromHexString("0x020000").ToUInt256(),
            ExtraData = Array.Empty<byte>(),
            GasLimit = (long)Bytes.FromHexString("0x2fefba").ToUnsignedBigInteger(),
            GasUsed = (long)Bytes.FromHexString("0x5208").ToUnsignedBigInteger(),
            MixHash = new Keccak(Bytes.FromHexString("0x00be1f287e0911ea2f070b3650a1a0346535895b6c919d7e992a0c255a83fc8b")),
            Nonce = (ulong)Bytes.FromHexString("0xa0ddc06c6d7b9f48").ToUnsignedBigInteger(),
            Number = (long)Bytes.FromHexString("0x01").ToUInt256(),
            ParentHash = new Keccak(Bytes.FromHexString("0x5a39ed1020c04d4d84539975b893a4e7c53eab6c2965db8bc3468093a31bc5ae")),
            ReceiptsRoot = new Keccak(Bytes.FromHexString("0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2")),
            StateRoot = new Keccak(Bytes.FromHexString("0x5c2e5a51a79da58791cdfe572bcfa3dfe9c860bf7fad7d9738a1aace56ef9332")),
            Timestamp = (ulong)Bytes.FromHexString("0x59d79f18").ToUnsignedBigInteger(),
            TxRoot = new Keccak(Bytes.FromHexString("0x5c9151c2413d1cd25c51ffb4ac38948acc1359bf08c6b49f283660e9bcf0f516")),
            UnclesHash = new Keccak(Bytes.FromHexString("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
        };

        Assert.AreEqual(new Keccak(Bytes.FromHexString("0x19a24085f6b1fb174aee0463264cc7163a7ffa165af04d3f40431ab3c3b08b98")), header.CalculateHash());
    }

    [Test]
    public void Hash_as_expected_2()
    {
        BlockHeader header = new()
        {
            Bloom = new Bloom(
            Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")),
            Beneficiary = new Address("0x8888f1f195afa192cfee860698584c030f4c9db1"),
            Difficulty = Bytes.FromHexString("0x020080").ToUInt256(),
            ExtraData = Array.Empty<byte>(),
            GasLimit = (long)Bytes.FromHexString("0x2fefba").ToUnsignedBigInteger(),
            GasUsed = (long)Bytes.FromHexString("0x5208").ToUnsignedBigInteger(),
            MixHash = new Keccak(Bytes.FromHexString("0x615bbf44eb133eab3cb24d5766ae9617d9e45ee00e7a5667db30672b47d22149")),
            Nonce = (ulong)Bytes.FromHexString("0x4c4f3d3e055cb264").ToUnsignedBigInteger(),
            Number = (long)Bytes.FromHexString("0x03").ToUInt256(),
            ParentHash = new Keccak(Bytes.FromHexString("0xde1457da701ef916533750d46c124e9ae50b974410bd590fbcf4c935a4d19465")),
            ReceiptsRoot = new Keccak(Bytes.FromHexString("0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2")),
            StateRoot = new Keccak(Bytes.FromHexString("0xfb4084a7f8b57e370fefe24a3da3aaea6c4dd8b6f6251916c32440336035160b")),
            Timestamp = (ulong)Bytes.FromHexString("0x59d79f1c").ToUnsignedBigInteger(),
            TxRoot = new Keccak(Bytes.FromHexString("0x1722b8a91bfc4f5614ce36ee77c7cce6620ab4af36d3c54baa66d7dbeb7bce1a")),
            UnclesHash = new Keccak(Bytes.FromHexString("0xe676a42c388d2d24bb2927605d5d5d82fba50fb60d74d44b1cd7d1c4e4eee3c0"))
        };
        header.Hash = header.CalculateHash();

        Assert.AreEqual(new Keccak(Bytes.FromHexString("0x1423c2875714c31049cacfea8450f66a73ecbd61d7a6ab13089406a491aa9fc2")), header.Hash);
    }

    [Test]
    public void Author()
    {
        Address author = new("0x05a56e2d52c817161883f50c441c3228cfe54d9f");

        BlockHeader header = new()
        {
            Beneficiary = author
        };

        Assert.AreEqual(author, header.GasBeneficiary);
    }

    [Test]
    public void Eip_1559_CalculateBaseFee_should_returns_zero_when_eip1559_not_enabled()
    {
        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
        releaseSpec.IsEip1559Enabled.Returns(false);

        BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
        blockHeader.Number = 2001;
        blockHeader.GasLimit = 100;
        UInt256 baseFee = BaseFeeCalculator.Calculate(blockHeader, releaseSpec);
        Assert.AreEqual(UInt256.Zero, baseFee);
    }

    [TestCase(100, 100, 88, 0)]
    [TestCase(100, 300, 267, 10)]
    [TestCase(500, 200, 185, 200)]
    [TestCase(500, 0, 0, 200)]
    [TestCase(21, 23, 23, 21)]
    [TestCase(21, 23, 61, 300)]
    [TestCase(500, 0, 10, 200, 10)]
    [TestCase(100, 100, 88, 0, 80)]
    [TestCase(100, 100, 110, 0, 110)]
    public void Eip_1559_CalculateBaseFee(long gasTarget, long baseFee, long expectedBaseFee, long gasUsed, long? minimalBaseFee = null)
    {
        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
        releaseSpec.IsEip1559Enabled.Returns(true);
        releaseSpec.Eip1559BaseFeeMinValue.Returns((UInt256?)minimalBaseFee);

        BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
        blockHeader.Number = 2001;
        blockHeader.GasLimit = gasTarget * Eip1559Constants.ElasticityMultiplier;
        blockHeader.BaseFeePerGas = (UInt256)baseFee;
        blockHeader.GasUsed = gasUsed;
        UInt256 actualBaseFee = BaseFeeCalculator.Calculate(blockHeader, releaseSpec);
        Assert.AreEqual((UInt256)expectedBaseFee, actualBaseFee);
    }

    [TestCaseSource(nameof(HasBodyTestSource))]
    public void Should_have_empty_body_as_expected((BlockHeader Header, bool HasBody) fixture) =>
        fixture.Header.HasBody.Should().Be(fixture.HasBody);

    public class BaseFeeTestCases
    {
        public int ParentBaseFee { get; set; }
        public int ParentGasUsed { get; set; }
        public int ParentTargetGasUsed { get; set; }
        public int ExpectedBaseFee { get; set; }
    }

    [TestCaseSource(nameof(Eip1559BaseFeeTestSource))]
    public void Eip_1559_CalculateBaseFee_shared_test_cases((BaseFeeTestCases Info, string Description) testCase)
    {
        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
        releaseSpec.IsEip1559Enabled.Returns(true);

        BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
        blockHeader.Number = 2001;
        blockHeader.GasLimit = testCase.Info.ParentTargetGasUsed * Eip1559Constants.ElasticityMultiplier;
        blockHeader.BaseFeePerGas = (UInt256)testCase.Info.ParentBaseFee;
        blockHeader.GasUsed = testCase.Info.ParentGasUsed;
        UInt256 actualBaseFee = BaseFeeCalculator.Calculate(blockHeader, releaseSpec);
        Assert.AreEqual((UInt256)testCase.Info.ExpectedBaseFee, actualBaseFee, testCase.Description);
    }

    private static IEnumerable<(BaseFeeTestCases, string)> Eip1559BaseFeeTestSource()
    {
        string testCases = File.ReadAllText("TestFiles/BaseFeeTestCases.json");
        BaseFeeTestCases[] deserializedTestCases = JsonConvert.DeserializeObject<BaseFeeTestCases[]>(testCases) ?? Array.Empty<BaseFeeTestCases>();

        for (int i = 0; i < deserializedTestCases.Length; ++i)
        {
            yield return (deserializedTestCases[i], $"Test case number {i}");
        }
    }

    private static IEnumerable<(BlockHeader, bool)> HasBodyTestSource() =>
        new[]
        {
            (new BlockHeader(), false),

            (new BlockHeader
            {
                TxRoot = Keccak.EmptyTreeHash,
                UnclesHash = Keccak.OfAnEmptySequenceRlp,
                WithdrawalsRoot = Keccak.EmptyTreeHash
            }, false),

            (new BlockHeader
            {
                TxRoot = Keccak.Zero,
                UnclesHash = Keccak.OfAnEmptySequenceRlp,
                WithdrawalsRoot = Keccak.EmptyTreeHash
            }, true),

            (new BlockHeader
            {
                UnclesHash = Keccak.Zero,
                WithdrawalsRoot = Keccak.EmptyTreeHash
            }, true),

            (new BlockHeader { WithdrawalsRoot = Keccak.Zero }, true),

            (new BlockHeader
            {
                TxRoot = Keccak.Zero,
                UnclesHash = Keccak.Zero,
                WithdrawalsRoot = Keccak.Zero
            }, true)
        };
}
