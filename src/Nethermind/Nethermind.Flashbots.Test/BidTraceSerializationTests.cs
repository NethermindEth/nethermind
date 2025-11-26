// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Flashbots.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Flashbots.Test
{
    [TestFixture]
    public class BidTraceSerializationTests
    {
        private readonly EthereumJsonSerializer _serializer = new();

        [Test]
        public void BidTracePublicKeys_DeserializedFromFullKey_SucceedWithExplicitConverter()
        {
            string json = """
                {
                    "slot": 12345,
                    "builder_public_key": "0xa49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365f52e5a8728693008d97ae83d51194f273455acf1a30e6f3926aefaede484c07d8ec3",
                    "proposer_public_key": "0xb49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365f52e5a8728693008d97ae83d51194f273455acf1a30e6f3926aefaede484c07d8ec4",
                    "value": "1000000000000000000"
                }
                """;

            BidTrace trace = _serializer.Deserialize<BidTrace>(json);

            trace.BuilderPublicKey.Should().NotBeNull();
            trace.ProposerPublicKey.Should().NotBeNull();
            trace.BuilderPublicKey!.ToString(false).Should().Be("a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365f52e5a8728693008d97ae83d51194f273455acf1a30e6f3926aefaede484c07d8ec3");
            trace.ProposerPublicKey!.ToString(false).Should().Be("b49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365f52e5a8728693008d97ae83d51194f273455acf1a30e6f3926aefaede484c07d8ec4");
        }
    }
}