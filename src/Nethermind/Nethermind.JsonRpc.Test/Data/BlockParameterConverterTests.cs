// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class BlockParameterConverterTests : SerializationTestBase
    {
        [TestCase("0", 0)]
        [TestCase("100", 100)]
        [TestCase("\"0x0\"", 0)]
        [TestCase("\"0xA\"", 10)]
        [TestCase("\"0xa\"", 10)]
        [TestCase("\"0\"", 0)]
        [TestCase("\"100\"", 100)]
        [TestCase("{ \"blockNumber\": \"0xa\" }", 10)]
        public void Can_read_block_number(string input, long output)
        {
            IJsonSerializer serializer = new EthereumJsonSerializer();

            BlockParameter blockParameter = serializer.Deserialize<BlockParameter>(input)!;

            Assert.That(blockParameter.BlockNumber, Is.EqualTo(output));
        }

        [TestCase("0", true)]
        [TestCase("100", true)]
        [TestCase("\"0x\"", false)]
        [TestCase("\"0x0\"", false)]
        [TestCase("\"0xA\"", false)]
        [TestCase("\"0xa\"", false)]
        [TestCase("\"0\"", true)]
        [TestCase("\"100\"", true)]
        [TestCase("{ \"blockNumber\": \"0xa\" }", false)]
        [TestCase("{ \"blockNumber\": \"100\" }", true)]
        public void Cant_read_block_number_when_strict_hex_format_is_enabled(string input, bool throws)
        {
            EthereumJsonSerializer.StrictHexFormat = true;
            IJsonSerializer serializer = new EthereumJsonSerializer();

            Func<BlockParameter> action = () => serializer.Deserialize<BlockParameter>(input);

            if (throws)
                action.Should().Throw<FormatException>();
            else
                action.Should().NotThrow();
        }

        [TestCase("null", BlockParameterType.Latest)]
        [TestCase("\"\"", BlockParameterType.Latest)]
        [TestCase("\"latest\"", BlockParameterType.Latest)]
        [TestCase("\"LATEst\"", BlockParameterType.Latest)]
        [TestCase("\"earliest\"", BlockParameterType.Earliest)]
        [TestCase("\"EaRlIEST\"", BlockParameterType.Earliest)]
        [TestCase("\"pending\"", BlockParameterType.Pending)]
        [TestCase("\"PeNdInG\"", BlockParameterType.Pending)]
        [TestCase("\"finalized\"", BlockParameterType.Finalized)]
        [TestCase("\"Finalized\"", BlockParameterType.Finalized)]
        [TestCase("\"safe\"", BlockParameterType.Safe)]
        [TestCase("\"Safe\"", BlockParameterType.Safe)]
        [TestCase("{ \"blockNumber\": \"\" }", BlockParameterType.Latest)]
        [TestCase("{ \"blockNumber\": \"latest\" }", BlockParameterType.Latest)]
        [TestCase("{ \"blockNumber\": \"LATEst\" }", BlockParameterType.Latest)]
        [TestCase("{ \"blockNumber\": \"earliest\" }", BlockParameterType.Earliest)]
        [TestCase("{ \"blockNumber\": \"EaRlIEST\" }", BlockParameterType.Earliest)]
        [TestCase("{ \"blockNumber\": \"pending\" }", BlockParameterType.Pending)]
        [TestCase("{ \"blockNumber\": \"PeNdInG\" }", BlockParameterType.Pending)]
        [TestCase("{ \"blockNumber\": \"finalized\" }", BlockParameterType.Finalized)]
        [TestCase("{ \"blockNumber\": \"Finalized\" }", BlockParameterType.Finalized)]
        [TestCase("{ \"blockNumber\": \"safe\" }", BlockParameterType.Safe)]
        [TestCase("{ \"blockNumber\": \"Safe\" }", BlockParameterType.Safe)]
        public void Can_read_type(string input, BlockParameterType output)
        {
            IJsonSerializer serializer = new EthereumJsonSerializer();

            BlockParameter blockParameter = serializer.Deserialize<BlockParameter>(input)!;

            Assert.That(blockParameter.Type, Is.EqualTo(output));
        }

        [TestCase("\"0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3\"", "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3", false)]
        [TestCase("{ \"blockHash\": \"0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3\" }", "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3", false)]
        [TestCase("{ \"blockHash\": \"0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3\", \"requireCanonical\": true  }", "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3", true)]
        public void Can_read_block_hash(string input, string output, bool requireCanonical)
        {
            IJsonSerializer serializer = new EthereumJsonSerializer();

            BlockParameter blockParameter = serializer.Deserialize<BlockParameter>(input)!;

            Assert.That(blockParameter.BlockHash, Is.EqualTo(new Hash256(output)));
            Assert.That(blockParameter.RequireCanonical, Is.EqualTo(requireCanonical));
        }

        [TestCase("\"latest\"", BlockParameterType.Latest)]
        [TestCase("\"earliest\"", BlockParameterType.Earliest)]
        [TestCase("\"pending\"", BlockParameterType.Pending)]
        [TestCase("null", BlockParameterType.BlockNumber)]
        [TestCase("null", BlockParameterType.BlockHash)]
        public void Can_write_type(string output, BlockParameterType input)
        {
            BlockParameter blockParameter = new(input);

            IJsonSerializer serializer = new EthereumJsonSerializer();

            var result = serializer.Serialize(blockParameter);

            Assert.That(result, Is.EqualTo(output));
        }

        [TestCase("\"0x0\"", 0)]
        [TestCase("\"0xa\"", 10)]
        public void Can_write_number(string output, long input)
        {
            BlockParameter blockParameter = new(input);

            IJsonSerializer serializer = new EthereumJsonSerializer();

            var result = serializer.Serialize(blockParameter);

            Assert.That(result, Is.EqualTo(output));
        }

        [Test]
        public void Can_do_roundtrip()
        {
            TestRoundtrip(BlockParameter.Latest, "latest");
            TestRoundtrip(BlockParameter.Pending, "pending");
            TestRoundtrip(BlockParameter.Earliest, "earliest");
            TestRoundtrip(BlockParameter.Finalized, "finalized");
            TestRoundtrip(BlockParameter.Safe, "safe");
            TestRoundtrip(new BlockParameter(0L), "zero");
            TestRoundtrip(new BlockParameter(long.MaxValue), "max");
            TestRoundtrip(new BlockParameter(TestItem.KeccakA), "hash");
            TestRoundtrip(new BlockParameter(TestItem.KeccakA, true), "hash with canonical");
        }
    }
}
