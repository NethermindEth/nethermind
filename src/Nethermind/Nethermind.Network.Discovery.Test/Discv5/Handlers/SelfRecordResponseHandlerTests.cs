// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5.Handlers;

public class SelfRecordResponseHandlerTests
{
    [Test]
    public void Repeated_self_records_verify_at_most_one_candidate(
        [Values("wrong-identity", "negated-identity", "stale", "invalid-signature", "honest")] string kind)
    {
        byte[] negatedBytes = new byte[32];
        (SecP256k1Curve.N - new UInt256(TestItem.PrivateKeyB.KeyBytes, isBigEndian: true)).ToBigEndian(negatedBytes);
        using PrivateKey negatedKey = new(negatedBytes);
        NodeRecord record = TestEnrBuilder.BuildSignedWithoutEndpoint(
            kind switch
            {
                "wrong-identity" => TestItem.PrivateKeyC,
                "negated-identity" => negatedKey,
                _ => TestItem.PrivateKeyB
            }, enrSequence: kind == "stale" ? 1UL : 2UL);
        if (kind == "invalid-signature") record.Signature = new Ecdsa().Sign(TestItem.PrivateKeyC, record.ContentHash);
        CountingEcdsa crypto = new();
        using SelfRecordResponseHandler handler = CreateHandler(crypto);
        using NodesMsg message = new([1], 16, Enumerable.Repeat(record, 16).ToArray());

        for (int index = 0; index < 17; index++) handler.Handle(message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(crypto.Recoveries, Is.EqualTo(kind is "wrong-identity" or "negated-identity" or "stale" ? 0 : kind == "honest" ? 1 : 2));
            Assert.That(handler.Task.IsCompletedSuccessfully, Is.True);
            Assert.That(handler.GetRecord(), kind == "honest" ? Is.SameAs(record) : Is.Null);
        }
    }

    [Test]
    public void Changing_total_completes_without_verifying_the_changed_message()
    {
        NodeRecord wrong = TestEnrBuilder.BuildSignedWithoutEndpoint(TestItem.PrivateKeyC, enrSequence: 2);
        NodeRecord honest = TestEnrBuilder.BuildSignedWithoutEndpoint(TestItem.PrivateKeyB, enrSequence: 2);
        CountingEcdsa crypto = new();
        using SelfRecordResponseHandler handler = CreateHandler(crypto);
        using NodesMsg first = new([1], 2, [wrong]);
        using NodesMsg changed = new([1], 3, [honest]);

        handler.Handle(first);
        handler.Handle(changed);
        handler.Handle(first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(crypto.Recoveries, Is.Zero);
            Assert.That(handler.Task.IsCompletedSuccessfully, Is.True);
            Assert.That(handler.GetRecord(), Is.Null);
        }
    }

    [Test]
    public void Invalid_total_does_not_verify_records([Values(0, 17)] int total)
    {
        NodeRecord record = TestEnrBuilder.BuildSignedWithoutEndpoint(TestItem.PrivateKeyB, enrSequence: 2);
        CountingEcdsa crypto = new();
        using SelfRecordResponseHandler handler = CreateHandler(crypto);
        using NodesMsg message = new([1], total, [record]);

        handler.Handle(message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(crypto.Recoveries, Is.Zero);
            Assert.That(handler.Task.IsCompletedSuccessfully, Is.True);
            Assert.That(handler.GetRecord(), Is.Null);
        }
    }

    [Test]
    public void Disposed_handler_does_not_verify_records()
    {
        NodeRecord record = TestEnrBuilder.BuildSignedWithoutEndpoint(TestItem.PrivateKeyB, enrSequence: 2);
        CountingEcdsa crypto = new();
        SelfRecordResponseHandler handler = CreateHandler(crypto);
        using NodesMsg message = new([1], 1, [record]);

        handler.Dispose();
        handler.Handle(message);

        Assert.That(crypto.Recoveries, Is.Zero);
    }

    private static SelfRecordResponseHandler CreateHandler(CountingEcdsa crypto)
        => new(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303), minimumSequence: 2, recordSigner: new NodeRecordSigner(crypto));
}
