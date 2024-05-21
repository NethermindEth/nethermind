// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[TestFixture, Parallelizable(ParallelScope.All)]
public class ReceiptsMessageSerializerTests
{
    private class EmptyTxReceipt : TxReceipt
    {
        public EmptyTxReceipt()
        {
            Logs = []; // Logs are always assumed non-null in decoders
        }
    }

    public static object[] TestData =
    [
        new object[]
        {
            1111,
            new TxReceipt[]
            {
                new EmptyTxReceipt
                {
                    GasUsedTotal = 1,
                    Logs =
                    [
                        new LogEntry(new Address("0x0000000000000000000000000000000000000011"), Bytes.FromHexString("0x0100ff"),
                        [
                            new Hash256("0x000000000000000000000000000000000000000000000000000000000000dead"),
                            new Hash256("0x000000000000000000000000000000000000000000000000000000000000beef")
                        ])
                    ]
                }
            },
            "f86c820457f867f865f8638001f85ff85d940000000000000000000000000000000000000011f842a0000000000000000000000000000000000000000000000000000000000000deada0000000000000000000000000000000000000000000000000000000000000beef830100ff"
        },
        new object[]
        {
            0,
            new TxReceipt[]
            {
                new EmptyTxReceipt()
            },
            "c780c5c4c38080c0"
        },
    ];

    [Theory]
    [TestCaseSource(nameof(TestData))]
    public void Roundtrip(long requestId, TxReceipt[] receipts, string expected)
    {
        using ReceiptsMessage69 message = new(requestId, new(new ArrayPoolList<TxReceipt[]>(1)
        {
            receipts
        }));

        var serializer = new ReceiptsMessageSerializer69(new TestSpecProvider(Prague.Instance));

        SerializerTester.TestZero(
            serializer,
            message,
            expected
        );
    }

    [Test]
    public void IgnoresBloom()
    {
        using ReceiptsMessage69 message = new(0, new(new ArrayPoolList<TxReceipt[]>(1)
        {
            new TxReceipt[]
            {
                new EmptyTxReceipt {Bloom = Bloom.Empty}
            }
        }));

        var serializer = new ReceiptsMessageSerializer69(new TestSpecProvider(Prague.Instance));
        byte[] encoded = serializer.Serialize(message);

        message.EthMessage.TxReceipts[0]![0].Bloom = null;
        serializer.Serialize(message).Should().Equal(encoded);

        message.EthMessage.TxReceipts[0]![0].Bloom = new(Enumerable.Range(0, 255).Select(i => (byte)i).ToArray());
        serializer.Serialize(message).Should().Equal(encoded);
    }
}
