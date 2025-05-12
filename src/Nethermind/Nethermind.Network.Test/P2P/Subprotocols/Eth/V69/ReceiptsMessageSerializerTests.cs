// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
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

[Parallelizable(ParallelScope.All)]
public class ReceiptsMessageSerializerTests
{
    private class EmptyTxReceipt : TxReceipt
    {
        public EmptyTxReceipt()
        {
            Logs = []; // Logs are always assumed non-null in decoders
        }
    }

    private static readonly object[] TestData =
    [
        new object[] // Empty legacy tx
        {
            0,
            new TxReceipt[] { new EmptyTxReceipt() },
            "c880c6c5c4808080c0"
        },
        new object[] // Empty EIP-1559 tx
        {
            1,
            new TxReceipt[] { new EmptyTxReceipt { TxType = TxType.EIP1559 } },
            "c801c6c5c4028080c0"
        },
        new object[] // Legacy tx with gas used and logs
        {
            2,
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
            "f86b02f868f866f864808001f85ff85d940000000000000000000000000000000000000011f842a0000000000000000000000000000000000000000000000000000000000000deada0000000000000000000000000000000000000000000000000000000000000beef830100ff"
        },
        new object[] // From Geth tests
        {
            3,
            new TxReceipt[]
            {
                new EmptyTxReceipt
                {
                    TxType = TxType.Legacy,
                    StatusCode = 1,
                    GasUsedTotal = 555,
                }
            },
            "ca03c8" + "c7c6800182022bc0"
        },
        new object[] // From Geth tests
        {
            3,
            new TxReceipt[]
            {
                new EmptyTxReceipt
                {
                    TxType = TxType.EIP1559,
                    StatusCode = 1,
                    GasUsedTotal = 555,
                }
            },
            "ca03c8" + "c7c6020182022bc0"
        },
        new object[] // From Geth tests
        {
            3,
            new TxReceipt[]
            {
                new EmptyTxReceipt
                {
                    TxType = TxType.AccessList,
                    StatusCode = 1,
                    GasUsedTotal = 555,
                }
            },
            "ca03c8" + "c7c6010182022bc0"
        },
        new object[] // From Geth tests
        {
            3,
            new TxReceipt[]
            {
                new EmptyTxReceipt
                {
                    TxType = TxType.Legacy,
                    StatusCode = 1,
                    GasUsedTotal = 555,
                    Logs =
                    [
                        new(
                            address: new(new byte[] { 1 }.PadRight(Address.Size)),
                            topics: [new(new byte[] { 1 }.PadRight(Hash256.Size))],
                            data: []
                        )
                    ]
                }
            },
            "f84803f845" + "f843f841800182022bf83af838940100000000000000000000000000000000000000e1a0010000000000000000000000000000000000000000000000000000000000000080"
        },
        new object[] // From Geth tests
        {
            3,
            new TxReceipt[]
            {
                new EmptyTxReceipt
                {
                    TxType = TxType.AccessList,
                    StatusCode = 1,
                    GasUsedTotal = 555,
                    Logs =
                    [
                        new(
                            address: new(new byte[] { 2 }.PadRight(Address.Size)),
                            topics: [new(new byte[] { 21 }.PadRight(Hash256.Size)), new(new byte[] { 22 }.PadRight(Hash256.Size))],
                            data: [2, 2, 32, 32]
                        ),
                        new(
                            address: new(new byte[] { 3 }.PadRight(Address.Size)),
                            topics: [new(new byte[] { 31 }.PadRight(Hash256.Size)), new(new byte[] { 32 }.PadRight(Hash256.Size))],
                            data: [3, 3, 32, 32]
                        )
                    ]
                }
            },
            "f8ce03f8cb" + "f8c9f8c7010182022bf8c0f85e940200000000000000000000000000000000000000f842a01500000000000000000000000000000000000000000000000000000000000000a016000000000000000000000000000000000000000000000000000000000000008402022020f85e940300000000000000000000000000000000000000f842a01f00000000000000000000000000000000000000000000000000000000000000a020000000000000000000000000000000000000000000000000000000000000008403032020"
        }
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

        var x = PooledByteBufferAllocator.Default.Buffer(1024);
        serializer.Serialize(x, message);

        SerializerTester.TestZero(
            serializer,
            message,
            expected
        );
    }

    [Theory]
    public void IgnoresBloom([Values(null, 255)] int? length)
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

        message.EthMessage.TxReceipts[0]![0].Bloom = length.HasValue
            ? new(Enumerable.Range(0, length.Value).Select(i => (byte)i).ToArray())
            : null;
        serializer.Serialize(message).Should().Equal(encoded);
    }
}
