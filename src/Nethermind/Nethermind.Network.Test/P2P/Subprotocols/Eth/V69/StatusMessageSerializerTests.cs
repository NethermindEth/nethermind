// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[TestFixture, Parallelizable(ParallelScope.All)]
public class StatusMessageSerializerTests
{
    public static object[] TestData =
    [
        new object[]
        {
            new StatusMessage
            {
                ProtocolVersion = 69,
                BestHash = Keccak.Compute("1"),
                GenesisHash = Keccak.Compute("0"),
                NetworkId = 1
            },
            "f8444501a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6a0044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116d"
        },
        new object[]
        {
            new StatusMessage(),
            "c480808080"
        },
    ];

    [Theory]
    [TestCaseSource(nameof(TestData))]
    public void Roundtrip(StatusMessage message, string expected)
    {
        var serializer = new StatusMessageSerializer();

        SerializerTester.TestZero(
            serializer,
            message,
            expected
        );
    }

    [Test]
    public void IgnoresTotalDifficulty()
    {
        var message = new StatusMessage
        {
            ProtocolVersion = 69,
            BestHash = Keccak.Compute("1"),
            GenesisHash = Keccak.Compute("0"),
            NetworkId = 1,
            TotalDifficulty = null
        };

        var serializer = new StatusMessageSerializer();
        byte[] encoded = serializer.Serialize(message);

        message.TotalDifficulty = 0;
        serializer.Serialize(message).Should().Equal(encoded);

        message.TotalDifficulty = 1;
        serializer.Serialize(message).Should().Equal(encoded);
    }
}
