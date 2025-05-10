// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[Parallelizable(ParallelScope.All)]
public class BlockRangeUpdateMessageSerializerTests
{
    private static object[] _testData =
    [
        new object[]
        {
            new BlockRangeUpdateMessage
            {
                EarliestBlock = 0,
                LatestBlock = 0,
                LatestBlockHash = Hash256.Zero
            },
            "e38080a00000000000000000000000000000000000000000000000000000000000000000"
        },
        new object[]
        {
            new BlockRangeUpdateMessage
            {
                EarliestBlock = long.MaxValue,
                LatestBlock = long.MaxValue,
                LatestBlockHash = new("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"),
            },
            "f3887fffffffffffffff887fffffffffffffffa0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        }
    ];

    [Theory]
    [TestCaseSource(nameof(_testData))]
    public void Roundtrip(BlockRangeUpdateMessage message, string expected)
    {
        var serializer = new BlockRangeUpdateMessageSerializer();

        SerializerTester.TestZero(
            serializer,
            message,
            expected
        );
    }
}
