// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[Parallelizable(ParallelScope.All)]
public class StatusMessageSerializerTests
{
    private static object[] _testData =
    [
        new object[]
        {
            new StatusMessage69
            {
                ProtocolVersion = 69,
                NetworkId = BlockchainIds.Mainnet,
                GenesisHash = Hash256.Zero,
                ForkId = new(0, 0),
                EarliestBlock = 0,
                LatestBlock = 0,
                LatestBlockHash = Hash256.Zero
            },
            "f84d4501a00000000000000000000000000000000000000000000000000000000000000000c68400000000808080a00000000000000000000000000000000000000000000000000000000000000000"
        },
        new object[]
        {
            new StatusMessage69
            {
                ProtocolVersion = 69,
                NetworkId = BlockchainIds.Mainnet,
                GenesisHash = new Hash256("044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116d"),
                ForkId = new(0, 1),
                EarliestBlock = 0,
                LatestBlock = 1,
                LatestBlockHash = new Hash256("c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6"),
            },
            "f84d4501a0044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116dc68400000000018001a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6"
        },
        new object[]
        {
            new StatusMessage69
            {
                ProtocolVersion = 69,
                NetworkId = BlockchainIds.Sepolia,
                GenesisHash = new("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"),
                ForkId = new(uint.MaxValue, ulong.MaxValue),
                EarliestBlock = long.MaxValue,
                LatestBlock = long.MaxValue,
                LatestBlockHash = new("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")
            },
            "f8684583aa36a7a0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffce84ffffffff88ffffffffffffffff887fffffffffffffff887fffffffffffffffa0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        }
    ];

    [Theory]
    [TestCaseSource(nameof(_testData))]
    public void Roundtrip(StatusMessage69 message, string expected)
    {
        var serializer = new StatusMessageSerializer69();

        SerializerTester.TestZero(
            serializer,
            message,
            expected
        );
    }
}
