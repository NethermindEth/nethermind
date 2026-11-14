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
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(55)]
    [TestCase(56)]
    [TestCase(255)]
    [TestCase(256)]
    [TestCase(65_535)]
    [TestCase(65_536)]
    [TestCase(2 * 1024 * 1024)]
    [TestCase(16_777_215)]
    [TestCase(16_777_216)]
    [TestCase(int.MaxValue - 5)]
    public void Gets_the_content_length_of_a_sequence(int contentLength) =>
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

    [TestCase(0)]
    [TestCase(57)]
    [TestCase(258)]
    [TestCase(65_539)]
    public void Rejects_a_length_that_no_sequence_has(int sequenceLength) =>
        Assert.That(() => MessageSequence.GetContentLength(sequenceLength), Throws.ArgumentException);

    [TestCaseSource(nameof(Serializers))]
    public void Serializer_rejects_a_destination_longer_than_the_message(Func<int> getLength, Func<byte[], int> serialize)
    {
        byte[] destination = new byte[getLength() + 1];

        Assert.That(() => serialize(destination), Throws.InstanceOf<ArgumentException>().Or.InstanceOf<InvalidOperationException>());
    }

    [TestCase(-300)]
    [TestCase(-100)]
    [TestCase(0)]
    public void Serializes_block_bodies_around_the_soft_limit(int dataLengthDelta)
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
        BlockBodiesMessage bodies = new([new BlockBody([Build.A.Transaction.Signed().TestObject], [])]);
        BlockBodiesMessageSerializer bodiesSerializer = new();
        yield return new TestCaseData((Func<int>)(() => bodiesSerializer.GetLength(bodies)), (Func<byte[], int>)(d => bodiesSerializer.Serialize(d, bodies)))
            .SetName("BlockBodies");

        ReceiptsMessage receipts = new(new ArrayPoolList<TxReceipt[]>(1) { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject } });
        ReceiptsMessageSerializer receiptsSerializer = new(MainnetSpecProvider.Instance);
        yield return new TestCaseData((Func<int>)(() => receiptsSerializer.GetLength(receipts)), (Func<byte[], int>)(d => receiptsSerializer.Serialize(d, receipts)))
            .SetName("Receipts");

        TransactionsMessage transactions = new(new ArrayPoolList<Transaction>(1) { Build.A.Transaction.Signed().TestObject });
        TransactionsMessageSerializer transactionsSerializer = new();
        yield return new TestCaseData((Func<int>)(() => transactionsSerializer.GetLength(transactions)), (Func<byte[], int>)(d => transactionsSerializer.Serialize(d, transactions)))
            .SetName("Transactions");

        BlockBodiesMessage66 bodies66 = new(1, bodies);
        BlockBodiesMessageSerializer66 bodiesSerializer66 = new();
        yield return new TestCaseData((Func<int>)(() => bodiesSerializer66.GetLength(bodies66)), (Func<byte[], int>)(d => bodiesSerializer66.Serialize(d, bodies66)))
            .SetName("BlockBodies66");
    }
}
