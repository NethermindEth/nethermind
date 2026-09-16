// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5.Handlers;

public class SelfRecordResponseHandlerTests
{
    [Test]
    public void Accepts_matching_endpointless_self_record()
    {
        Node receiver = new(TestItem.PublicKeyB, "127.0.0.1", 30303);
        NodeRecord record = TestEnrBuilder.BuildSignedWithoutEndpoint(TestItem.PrivateKeyB, enrSequence: 2);
        using SelfRecordResponseHandler handler = new(receiver, minimumSequence: 2);
        using NodesMsg nodes = new([1], 1, [record]);

        handler.Handle(nodes);

        Assert.That(handler.GetRecord(), Is.SameAs(record));
    }

    [TestCase(true, 1UL)]
    [TestCase(false, 2UL)]
    public void Rejects_stale_or_wrong_identity_self_record(bool matchingIdentity, ulong sequence)
    {
        Node receiver = new(TestItem.PublicKeyB, "127.0.0.1", 30303);
        NodeRecord record = TestEnrBuilder.BuildSignedWithoutEndpoint(
            matchingIdentity ? TestItem.PrivateKeyB : TestItem.PrivateKeyC,
            enrSequence: sequence);
        using SelfRecordResponseHandler handler = new(receiver, minimumSequence: 2);
        using NodesMsg nodes = new([1], 1, [record]);

        handler.Handle(nodes);

        Assert.That(handler.GetRecord(), Is.Null);
    }
}
