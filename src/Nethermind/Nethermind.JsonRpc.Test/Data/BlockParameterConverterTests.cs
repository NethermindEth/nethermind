// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Data;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class BlockParameterConverterTests : SerializationTestBase
    {
        [TestCase("0x0", 0)]
        [TestCase("0xA", 10)]
        [TestCase("0xa", 10)]
        [TestCase("0", 0)]
        [TestCase("100", 100)]
        [TestCase("\"0x0\"", 0)]
        [TestCase("\"0xA\"", 10)]
        [TestCase("\"0xa\"", 10)]
        [TestCase("\"0\"", 0)]
        [TestCase("\"100\"", 100)]
        public void Can_read_block_number(string input, long output)
        {
            using StringReader reader = new(input);
            using JsonTextReader textReader = new(reader);

            JsonSerializer serializer = new();
            BlockParameterConverter converter = new();
            serializer.Converters.Add(converter);
            BlockParameter blockParameter = serializer.Deserialize<BlockParameter>(textReader)!;

            Assert.AreEqual(output, blockParameter.BlockNumber);
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
            using StringReader reader = new(input);
            using JsonTextReader textReader = new(reader);

            JsonSerializer serializer = new();
            BlockParameterConverter converter = new();
            serializer.Converters.Add(converter);
            BlockParameter blockParameter = serializer.Deserialize<BlockParameter>(textReader)!;

            Assert.AreEqual(output, blockParameter.Type);
        }

        [TestCase("\"latest\"", BlockParameterType.Latest)]
        [TestCase("\"earliest\"", BlockParameterType.Earliest)]
        [TestCase("\"pending\"", BlockParameterType.Pending)]
        [TestCase("null", BlockParameterType.BlockNumber)]
        [TestCase("null", BlockParameterType.BlockHash)]
        public void Can_write_type(string output, BlockParameterType input)
        {
            BlockParameter blockParameter = new(input);

            using StringWriter reader = new();
            using JsonTextWriter textWriter = new(reader);

            JsonSerializer serializer = new();
            BlockParameterConverter converter = new();
            serializer.Converters.Add(converter);
            serializer.Serialize(textWriter, blockParameter);

            Assert.AreEqual(output, reader.ToString());
        }

        [TestCase("\"0x0\"", 0)]
        [TestCase("\"0xa\"", 10)]
        public void Can_write_number(string output, long input)
        {
            BlockParameter blockParameter = new(input);

            using StringWriter reader = new();
            using JsonTextWriter textWriter = new(reader);

            JsonSerializer serializer = new();
            BlockParameterConverter converter = new();
            serializer.Converters.Add(converter);
            serializer.Serialize(textWriter, blockParameter);

            Assert.AreEqual(output, reader.ToString());
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
