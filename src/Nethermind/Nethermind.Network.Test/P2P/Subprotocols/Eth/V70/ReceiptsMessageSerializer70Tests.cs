// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V70;

[Parallelizable(ParallelScope.All)]
public class ReceiptsMessageSerializer70Tests
{
    [TestCaseSource(nameof(RoundtripCases))]
    public void Roundtrip(Func<ReceiptsMessage70> buildMessage, string expectedData)
    {
        ReceiptsMessageSerializer70 serializer = new(new TestSpecProvider(Prague.Instance));

        SerializerTester.TestZero(serializer, buildMessage(), expectedData);
    }

    [Test]
    public void Deserialize_rejects_invalid_lastBlockIncomplete()
    {
        ReceiptsMessageSerializer70 serializer = new(new TestSpecProvider(Prague.Instance));
        byte[] payload = [0xc3, 0x80, 0x02, 0xc0];

        Assert.That(() => serializer.Deserialize(payload, out _), Throws.InstanceOf<RlpException>());
    }

    private static IEnumerable<TestCaseData> RoundtripCases()
    {
        // eth/70 (EIP-7975) Receipts: [request-id, lastBlockIncomplete: {0,1}, [[receipt, ...], ...]].
        yield return new TestCaseData(
                new Func<ReceiptsMessage70>(() => new(0, ArrayPoolList<TxReceipt[]>.Empty(), lastBlockIncomplete: false)),
                "c38080c0")
            .SetName("Roundtrip_empty_receipts_complete");
        yield return new TestCaseData(
                new Func<ReceiptsMessage70>(() => new(0, ArrayPoolList<TxReceipt[]>.Empty(), lastBlockIncomplete: true)),
                "c38001c0")
            .SetName("Roundtrip_empty_receipts_incomplete");
        yield return new TestCaseData(
                new Func<ReceiptsMessage70>(() => new(3, BuildSingleReceiptBlock(), lastBlockIncomplete: false)),
                "cb0380c8c7c6800182022bc0")
            .SetName("Roundtrip_single_receipt_complete");
        yield return new TestCaseData(
                new Func<ReceiptsMessage70>(() => new(3, BuildSingleReceiptBlock(), lastBlockIncomplete: true)),
                "cb0301c8c7c6800182022bc0")
            .SetName("Roundtrip_single_receipt_incomplete");
    }

    private static IOwnedReadOnlyList<TxReceipt[]> BuildSingleReceiptBlock() =>
        new ArrayPoolList<TxReceipt[]>(1)
        {
            new TxReceipt[]
            {
                new() { TxType = TxType.Legacy, StatusCode = 1, GasUsedTotal = 555, Logs = [] }
            }
        };
}
