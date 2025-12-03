// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
        public void BidTrace_WithFullPublicKeysJson_DeserializesSuccessfully()
        {
            const string builderKey = "a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365f52e5a8728693008d97ae83d51194f273455acf1a30e6f3926aefaede484c07d8ec3";
            const string proposerKey = "b49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365f52e5a8728693008d97ae83d51194f273455acf1a30e6f3926aefaede484c07d8ec4";
            byte[] builderKeyBytes = Bytes.FromHexString(builderKey);
            byte[] proposerKeyBytes = Bytes.FromHexString(proposerKey);

            string fullKeyJson = $$"""
                {
                    "slot": 12345,
                    "builder_public_key": "0x{{builderKey}}",
                    "proposer_public_key": "0x{{proposerKey}}",
                    "value": "1000000000000000000"
                }
                """;

            BidTrace trace = _serializer.Deserialize<BidTrace>(fullKeyJson);

            trace.BuilderPublicKey.Should().NotBeNull();
            trace.ProposerPublicKey.Should().NotBeNull();
            trace.BuilderPublicKey.Bytes.Length.Should().Be(64);
            trace.ProposerPublicKey.Bytes.Length.Should().Be(64);
            trace.BuilderPublicKey.Bytes.Should().BeEquivalentTo(builderKeyBytes);
            trace.ProposerPublicKey.Bytes.Should().BeEquivalentTo(proposerKeyBytes);
        }
    }
}