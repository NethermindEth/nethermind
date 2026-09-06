// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using NSubstitute;
using NUnit.Framework;
using GetBlockHeadersMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockHeadersMessage;

namespace Nethermind.Network.Test;

public class MessageDictionaryTests
{
    private readonly List<Eth66Message<GetBlockHeadersMessage>> _recordedRequests = [];
    private MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>>
        _testMessageDictionary;

    [SetUp]
    public void Setup()
    {
        _recordedRequests.Clear();
        _testMessageDictionary = new(RecordingProtocolHandler.Create(_recordedRequests));
    }

    [Test]
    public void Test_SendAndReceive()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);

        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        _testMessageDictionary.Send(request);

        Assert.That(_recordedRequests.Count, Is.EqualTo(1));
        Assert.That(_recordedRequests[^1], Is.SameAs(request.Message));
        Assert.That(request.CompletionSource.Task.IsCompleted, Is.False);

        _testMessageDictionary.Handle(111, response, 100);
        Assert.That(request.CompletionSource.Task.IsCompleted, Is.True);
        Assert.That(request.CompletionSource.Task.Result, Is.SameAs(response));
    }

    [Test]
    public void Test_SendAndReceive_withDifferentRequestId()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);

        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        _testMessageDictionary.Send(request);

        Assert.That(_recordedRequests.Count, Is.EqualTo(1));
        Assert.That(_recordedRequests[^1], Is.SameAs(request.Message));
        Assert.That(request.CompletionSource.Task.IsCompleted, Is.False);

        Assert.That(() => _testMessageDictionary.Handle(112, response, 100), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void Test_SendAndReceive_outOfOrder()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> requestBefore = CreateRequest(112);
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);

        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        _testMessageDictionary.Send(requestBefore);
        _testMessageDictionary.Send(request);

        Assert.That(_recordedRequests.Count, Is.EqualTo(2));
        Assert.That(_recordedRequests[^1], Is.SameAs(request.Message));
        Assert.That(request.CompletionSource.Task.IsCompleted, Is.False);

        _testMessageDictionary.Handle(111, response, 100);
        Assert.That(request.CompletionSource.Task.IsCompleted, Is.True);
        Assert.That(request.CompletionSource.Task.Result, Is.SameAs(response));

        Assert.That(requestBefore.CompletionSource.Task.IsCompleted, Is.False);
    }

    [Test]
    public void Test_Send_withTooManyConcurrentRequest()
    {
        for (int i = 0; i < 32; i++)
        {
            _testMessageDictionary.Send(CreateRequest(i));
        }

        Assert.That(() => _testMessageDictionary.Send(CreateRequest(33)), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Test_Send_MessageDisposing_OnInvalidId()
    {
        IOwnedReadOnlyList<BlockHeader> response = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();
        Assert.That(() => _testMessageDictionary.Handle(1234, response, 100), Throws.TypeOf<SubprotocolException>());

        response.Received().Dispose();
    }

    [Test]
    public void Test_Send_MessageDisposing_OnOldRequest()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);

        _testMessageDictionary.Send(request);

        // Simulate a request timed out
        request.CompletionSource.TrySetException(new TimeoutException());

        IOwnedReadOnlyList<BlockHeader> response = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();
        _testMessageDictionary.Handle(111, response, 100);

        response.Received().Dispose();
    }

    [Test]
    public void Handle_disposes_tuple_disposable_component_on_unmatched_request()
    {
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, (IDisposable, long)> dictionary = new(RecordingProtocolHandler.Create<Eth66Message<GetBlockHeadersMessage>>());
        IDisposable inner = Substitute.For<IDisposable>();

        Assert.That(() => dictionary.Handle(9999, (inner, 100L), 100), Throws.TypeOf<SubprotocolException>());

        inner.Received().Dispose();
    }

    private static Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> CreateRequest(int requestId)
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request =
            new(new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(requestId,
                new GetBlockHeadersMessage()));
        return request;
    }
}
