// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;
using BlockBodiesMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessage;
using BlockBodiesMessageSerializer66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessageSerializer;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class MessageSequenceTests
{
    [Test]
    public void Gets_the_content_length_of_a_sequence(
        [Values(0, 1, 55, 56, 255, 256, 65_535, 65_536, 2 * 1024 * 1024, 16_777_215, 16_777_216, int.MaxValue - 5)] int contentLength) =>
        Assert.That(MessageSequence.GetContentLength(Rlp.LengthOfSequence(contentLength)), Is.EqualTo(contentLength));

    [Test]
    public void Gets_every_content_length_up_to_three_length_bytes()
    {
        for (int contentLength = 0; contentLength < 70_000; contentLength++)
        {
            if (MessageSequence.GetContentLength(Rlp.LengthOfSequence(contentLength)) != contentLength)
            {
                Assert.Fail($"Content length {contentLength}");
            }
        }
    }

    [Test]
    public void Rejects_a_length_that_no_sequence_has([Values(0, 57, 258, 65_539)] int sequenceLength) =>
        Assert.That(() => MessageSequence.GetContentLength(sequenceLength), Throws.ArgumentException);

    [TestCaseSource(nameof(Serializers))]
    public void Serializer_rejects_a_destination_longer_than_the_message(Action serializeIntoLongerDestination) =>
        Assert.That(() => serializeIntoLongerDestination(), Throws.InstanceOf<ArgumentException>().Or.InstanceOf<InvalidOperationException>());

    [Test]
    public void Serializes_block_bodies_around_the_soft_limit([Values(-300, -100, 0)] int dataLengthDelta)
    {
        Transaction tx = Build.A.Transaction.WithData(new byte[(int)2.MiB + dataLengthDelta]).Signed().TestObject;
        using BlockBodiesMessage66 message = new(1, new BlockBodiesMessage([new BlockBody([tx], [])]));
        BlockBodiesMessageSerializer66 serializer = new();
        MessageSerializationService service = new(SerializerInfo.Create(serializer));

        byte[] encoded = service.SerializeToArray(message);
        using BlockBodiesMessage66 decoded = serializer.Deserialize(encoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Has.Length.EqualTo(serializer.GetLength(message)));
            Assert.That(decoded.RequestId, Is.EqualTo(1));
            Assert.That(decoded.EthMessage.Bodies!.Bodies[0]!.Transactions[0].Data.ToArray(), Is.EqualTo(tx.Data.ToArray()));
        }
    }

    private static IEnumerable<TestCaseData> Serializers()
    {
        yield return new TestCaseData((Action)(static () =>
        {
            using BlockBodiesMessage bodies = new([new BlockBody([Build.A.Transaction.Signed().TestObject], [])]);
            SerializeIntoLongerDestination(new BlockBodiesMessageSerializer(), bodies);
        })).SetName("BlockBodies");

        yield return new TestCaseData((Action)(static () =>
        {
            ArrayPoolList<byte> encoded = new(0);
            BlockBodyDecoder.Instance.EncodeTo(encoded, new BlockBody([Build.A.Transaction.Signed().TestObject], []));
            using BlockBodiesMessage bodies = new(encoded, 1);
            SerializeIntoLongerDestination(new BlockBodiesMessageSerializer(), bodies);
        })).SetName("EncodedBlockBodies");

        yield return new TestCaseData((Action)(static () =>
        {
            using ReceiptsMessage receipts = new(new ArrayPoolList<TxReceipt[]>(1) { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject } });
            SerializeIntoLongerDestination(new ReceiptsMessageSerializer(MainnetSpecProvider.Instance), receipts);
        })).SetName("Receipts");

        yield return new TestCaseData((Action)(static () =>
        {
            using TransactionsMessage transactions = new(new ArrayPoolList<Transaction>(1) { Build.A.Transaction.Signed().TestObject });
            SerializeIntoLongerDestination(new TransactionsMessageSerializer(), transactions);
        })).SetName("Transactions");

        yield return new TestCaseData((Action)(static () =>
        {
            using BlockBodiesMessage66 bodies66 = new(1, new BlockBodiesMessage([new BlockBody([Build.A.Transaction.Signed().TestObject], [])]));
            SerializeIntoLongerDestination(new BlockBodiesMessageSerializer66(), bodies66);
        })).SetName("BlockBodies66");
    }

    private static void SerializeIntoLongerDestination<T>(IMessageSerializer<T> serializer, T message) where T : MessageBase =>
        serializer.Serialize(new byte[serializer.GetLength(message) + 1], message);
}
