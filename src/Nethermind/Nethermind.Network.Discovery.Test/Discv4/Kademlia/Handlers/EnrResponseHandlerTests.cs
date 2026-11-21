// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4.Kademlia.Handlers;

public class EnrResponseHandlerTests
{
    private static readonly IPEndPoint Endpoint = new(IPAddress.Parse("203.0.113.10"), 30303);

    [Test]
    public void Response_from_the_queried_endpoint_completes_the_request()
    {
        EnrResponseHandler handler = CreateHandler();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Handle(CreateResponse(Endpoint)), Is.True);
            Assert.That(handler.TaskCompletionSource.Task.IsCompleted, Is.True);
        }
    }

    [Test]
    public void Response_from_another_endpoint_is_ignored()
    {
        EnrResponseHandler handler = CreateHandler();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Handle(CreateResponse(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 30303))), Is.False);
            Assert.That(handler.TaskCompletionSource.Task.IsCompleted, Is.False);
        }
    }

    [Test]
    public void Response_without_a_source_endpoint_is_ignored()
    {
        EnrResponseHandler handler = CreateHandler();
        EnrResponseMsg response = CreateResponse(Endpoint);
        response.FarAddress = null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Handle(response), Is.False);
            Assert.That(handler.TaskCompletionSource.Task.IsCompleted, Is.False);
        }
    }

    private static EnrResponseHandler CreateHandler()
    {
        EnrRequestMsg request = new(Endpoint, 0) { Hash = TestItem.KeccakA.ValueHash256 };
        return new EnrResponseHandler(request, Endpoint);
    }

    private static EnrResponseMsg CreateResponse(IPEndPoint farAddress)
    {
        NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, Endpoint.Address);
        return new EnrResponseMsg(farAddress, record, TestItem.KeccakA);
    }
}
