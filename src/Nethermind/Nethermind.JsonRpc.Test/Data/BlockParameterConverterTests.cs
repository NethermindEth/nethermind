// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [TestFixture]
    public class BlockParameterConverterTests : SerializationTestBase
    {
        // This fixture used to set EthereumJsonSerializer.StrictHexFormat in SetUp and put it back in TearDown.
        // That static is process-global: while this fixture held it at false, every other fixture parsing a block
        // parameter concurrently saw false too, so tests such as "Block number boundary leading zero" and
        // "leading zero in gasLimit" failed with -32603/-32602 in roughly a third of runs (#13204).
        // [NonParallelizable] did not help - NUnit only keeps such a test off a worker thread, it does not stop the
        // rest of the assembly from running alongside it.
        //
        // Strictness is a property of the converter now, so these tests name the strictness they want in the
        // options they parse with and touch no shared state. An options-level converter takes precedence over the
        // [JsonConverter] attribute on BlockParameter.
        private static JsonSerializerOptions OptionsWithStrictness(bool strictQuantity) =>
            new() { Converters = { new BlockParameterConverter(strictQuantity) } };

        private static BlockParameter? Deserialize(string input, bool strictQuantity) =>
            JsonSerializer.Deserialize<BlockParameter>(input, OptionsWithStrictness(strictQuantity));

        [TestCase("0", 0UL)]
        [TestCase("100", 100UL)]
        [TestCase("\"0x0\"", 0UL)]
        [TestCase("\"0xA\"", 10UL)]
        [TestCase("\"0xa\"", 10UL)]
        [TestCase("\"0\"", 0UL)]
        [TestCase("\"100\"", 100UL)]
        [TestCase("{ \"blockNumber\": \"0xa\" }", 10UL)]
        public void Can_read_block_number(string input, ulong output)
        {
            BlockParameter blockParameter = Deserialize(input, strictQuantity: false)!;

            Assert.That(blockParameter.BlockNumber, Is.EqualTo(output));
        }

        [TestCase("0", true)]
        [TestCase("100", true)]
        [TestCase("\"0x\"", true)]
        [TestCase("\"0x0\"", false)]
        [TestCase("\"0xA\"", false)]
        [TestCase("\"0xa\"", false)]
        [TestCase("\"0\"", true)]
        [TestCase("\"100\"", true)]
        [TestCase("{ \"blockNumber\": \"0xa\" }", false)]
        [TestCase("{ \"blockNumber\": \"100\" }", true)]
        public void Cant_read_block_number_when_strict_hex_format_is_enabled(string input, bool throws)
        {
            Func<BlockParameter?> action = () => Deserialize(input, strictQuantity: true);

            if (throws)
                Assert.That(action, Throws.InstanceOf<FormatException>());
            else
                Assert.That(action, Throws.Nothing);
        }

        // The regression guard for #13204. Before strictness became a property of the converter it came from one
        // process-global flag, so these two parses could not disagree: whichever value was set last won for both,
        // and a fixture that set it leniently changed how every concurrent fixture parsed. A leading-zero hex
        // quantity is rejected by EIP-1474 and accepted leniently, so it tells the two apart.
        [Test]
        public void Two_converters_can_disagree_about_strictness_at_the_same_time()
        {
            JsonSerializerOptions strict = OptionsWithStrictness(strictQuantity: true);
            JsonSerializerOptions lenient = OptionsWithStrictness(strictQuantity: false);

            Assert.That(
                () => JsonSerializer.Deserialize<BlockParameter>("\"0x0a\"", strict),
                Throws.InstanceOf<FormatException>());
            Assert.That(
                JsonSerializer.Deserialize<BlockParameter>("\"0x0a\"", lenient)!.BlockNumber,
                Is.EqualTo(10UL));
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

        [Test]
        public void Cannot_read_object_with_both_block_hash_and_block_number()
        {
            IJsonSerializer serializer = new EthereumJsonSerializer();

            Action action = () => serializer.Deserialize<BlockParameter>(
                """{ "blockNumber": "0xa", "blockHash": "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3" }""");

            Assert.That(
                action,
                Throws.InstanceOf<FormatException>()
                    .With.Message.EqualTo("cannot specify both BlockHash and BlockNumber, choose one or the other"));
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

            string result = serializer.Serialize(blockParameter);

            Assert.That(result, Is.EqualTo(output));
        }

        [TestCase("\"0x0\"", 0UL)]
        [TestCase("\"0xa\"", 10UL)]
        public void Can_write_number(string output, ulong input)
        {
            BlockParameter blockParameter = new(input);

            IJsonSerializer serializer = new EthereumJsonSerializer();

            string result = serializer.Serialize(blockParameter);

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
            TestRoundtrip(new BlockParameter(0UL), "zero");
            TestRoundtrip(new BlockParameter(ulong.MaxValue), "max");
            TestRoundtrip(new BlockParameter(TestItem.KeccakA), "hash");
            TestRoundtrip(new BlockParameter(TestItem.KeccakA, true), "hash with canonical");
        }
    }
}
