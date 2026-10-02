// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;
using ReceiptsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessage;
using ReceiptsMessageSerializer66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessageSerializer;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63;

[Parallelizable(ParallelScope.All)]
public class ReceiptsResponseBudgetTests
{
    private const long RequestId = 1111;

    [Test]
    public void Response_is_checked_against_the_request(
        [ValueSource(nameof(Envelopes))] Envelope envelope,
        [ValueSource(nameof(Scenarios))] Scenario scenario)
    {
        using DisposableByteBuffer content = envelope.Encode(BuildBlocks(scenario.ReceiptsPerBlock)).AsDisposable();

        Action check = () => ReceiptsResponseBudget.ThrowIfExceeded(content, envelope.FieldsBeforeReceipts, scenario.RequestedBlocks, scenario.MaxReceiptsPerBlock);

        Assert.That(check, scenario.Rejected
            ? Throws.TypeOf<SubprotocolException>().With.Message.Contains("exceeds the request")
            : Throws.Nothing);
    }

    [TestCase(new byte[] { }, null, TestName = "Empty response")]
    [TestCase(new byte[] { 0xc1 }, 1, TestName = "Envelope shorter than its header")]
    [TestCase(new byte[] { 0xc3, 0x01, 0xc5, 0xc0 }, 1, TestName = "Block list shorter than its header")]
    [TestCase(new byte[] { 0xc4, 0x01, 0xc2, 0xc3, 0x01 }, 1, TestName = "Block shorter than its header")]
    public void Truncated_response_is_rejected_as_malformed(byte[] payload, int? fieldsBeforeReceipts)
    {
        using DisposableByteBuffer content = Unpooled.WrappedBuffer(payload).AsDisposable();

        Assert.That(() => ReceiptsResponseBudget.ThrowIfExceeded(content, fieldsBeforeReceipts, 1, [5]), Throws.TypeOf<RlpException>());
    }

    [TestCase(null, 0, 2, null)]
    [TestCase(new[] { 3, 2, 1 }, 1, 2, new[] { 2, 1 })]
    [TestCase(new[] { 3 }, 0, 2, new[] { 3, -1 })]
    public void Slice_takes_the_limits_of_the_requested_blocks(int[]? expectedReceiptCounts, int start, int count, int[]? expected) =>
        Assert.That(ReceiptsResponseBudget.Slice(expectedReceiptCounts, start, count), Is.EqualTo(expected));

    private static ArrayPoolList<TxReceipt[]> BuildBlocks(int[] receiptsPerBlock) =>
        receiptsPerBlock.Select(static count => Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, count).ToArray()).ToPooledList(receiptsPerBlock.Length);

    private static IEnumerable<Envelope> Envelopes()
    {
        yield return new Envelope("eth/63", null, static blocks =>
        {
            using ReceiptsMessage message = new(blocks);
            return Unpooled.WrappedBuffer(new ReceiptsMessageSerializer(MainnetSpecProvider.Instance).Serialize(message));
        });
        yield return new Envelope("eth/66", 1, static blocks =>
        {
            using ReceiptsMessage66 message = new(RequestId, new ReceiptsMessage(blocks));
            return Unpooled.WrappedBuffer(new ReceiptsMessageSerializer66(new ReceiptsMessageSerializer(MainnetSpecProvider.Instance)).Serialize(message));
        });
        yield return new Envelope("eth/70", 2, static blocks =>
        {
            using ReceiptsMessage70 message = new(RequestId, blocks, true);
            return Unpooled.WrappedBuffer(new ReceiptsMessageSerializer70(MainnetSpecProvider.Instance).Serialize(message));
        });
    }

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return new Scenario("receipts match the counts", [2, 1], 2, [2, 1], false);
        yield return new Scenario("fewer receipts and blocks than allowed", [1], 2, [2, 1], false);
        yield return new Scenario("one receipt too many in the first block", [3, 1], 2, [2, 1], true);
        yield return new Scenario("one receipt too many in the last block", [2, 2], 2, [2, 1], true);
        yield return new Scenario("more blocks than requested", [1, 1, 1], 2, null, true);
        yield return new Scenario("more blocks than requested with counts", [1, 1, 1], 2, [1, 1], true);
        yield return new Scenario("counts unknown per block", [5, 5], 2, [-1, -1], false);
        yield return new Scenario("no counts", [5, 5], 2, null, false);
        yield return new Scenario("empty block over a zero count", [0], 1, [0], false);
        yield return new Scenario("receipts over a zero count", [1], 1, [0], true);
    }

    public sealed record Envelope(string Name, int? FieldsBeforeReceipts, Func<ArrayPoolList<TxReceipt[]>, IByteBuffer> Encode)
    {
        public override string ToString() => Name;
    }

    public sealed record Scenario(string Name, int[] ReceiptsPerBlock, int RequestedBlocks, int[]? MaxReceiptsPerBlock, bool Rejected)
    {
        public override string ToString() => Name;
    }
}
