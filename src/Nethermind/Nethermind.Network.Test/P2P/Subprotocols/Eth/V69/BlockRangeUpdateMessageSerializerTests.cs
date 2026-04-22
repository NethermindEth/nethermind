// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[Parallelizable(ParallelScope.All)]
public class BlockRangeUpdateMessageSerializerTests
{
    private static IEnumerable<TestCaseData> _testData =
    [
        new TestCaseData(
            new BlockRangeUpdateMessage
            {
                EarliestBlock = 0,
                LatestBlock = 0,
                LatestBlockHash = Hash256.Zero
            },
            "e38080a00000000000000000000000000000000000000000000000000000000000000000")
            .SetName("Zero values"),
        new TestCaseData(
            new BlockRangeUpdateMessage
            {
                EarliestBlock = long.MaxValue,
                LatestBlock = long.MaxValue,
                LatestBlockHash = new("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"),
            },
            "f3887fffffffffffffff887fffffffffffffffa0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            .SetName("Max values"),
    ];

    [Theory]
    [TestCaseSource(nameof(_testData))]
    public void Roundtrip(BlockRangeUpdateMessage message, string expected)
    {
        BlockRangeUpdateMessageSerializer serializer = new();

        SerializerTester.TestZero(
            serializer,
            message,
            expected
        );
    }
}
