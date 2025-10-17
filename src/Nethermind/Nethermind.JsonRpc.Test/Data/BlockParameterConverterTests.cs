// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Find;
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
        public void Cant_read_block_number_when_follow_standardization_rules(string input, bool throws)
        {
            EthereumJsonSerializer.FollowStandardizationRules = true;
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
        public void Can_read_type(string input, BlockParameterType output)
        {
            IJsonSerializer serializer = new EthereumJsonSerializer();

            BlockParameter blockParameter = serializer.Deserialize<BlockParameter>(input)!;

            Assert.That(blockParameter.Type, Is.EqualTo(output));
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
