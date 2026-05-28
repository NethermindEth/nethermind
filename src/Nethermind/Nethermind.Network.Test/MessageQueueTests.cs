// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class MessageQueueTests
{
    private readonly List<GetBlockHeadersMessage> _recordedSends = [];
    private MessageQueue<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> _queue;

    [SetUp]
    public void Setup()
    {
        _recordedSends.Clear();
        _queue = new(RecordingProtocolHandler.Create(_recordedSends));
    }

    [Test]
    public void Send_first_request_is_sent_immediately()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest();

        _queue.Send(request);

        Assert.That(_recordedSends.Count, Is.EqualTo(1));
        Assert.That(request.CompletionSource.Task.IsCompleted, Is.False);
    }

    [Test]
    public void Send_second_request_is_queued()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request1 = CreateRequest();
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request2 = CreateRequest();

        _queue.Send(request1);
        _queue.Send(request2);

        Assert.That(_recordedSends.Count, Is.EqualTo(1));
        Assert.That(request2.CompletionSource.Task.IsCompleted, Is.False);
    }

    [Test]
    public void Handle_completes_current_request_and_sends_next()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request1 = CreateRequest();
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request2 = CreateRequest();

        IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        _queue.Send(request1);
        _queue.Send(request2);

        _queue.Handle(response, 100);

        Assert.That(request1.CompletionSource.Task.IsCompleted, Is.True);
        Assert.That(request1.CompletionSource.Task.Result, Is.SameAs(response));
        Assert.That(_recordedSends.Count, Is.EqualTo(2));
    }

    [Test]
    public void Handle_throws_when_no_current_request()
    {
        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        Assert.That(() => _queue.Handle(response, 100), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void Handle_disposes_data_when_no_current_request()
    {
        IOwnedReadOnlyList<BlockHeader> response = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();

        Assert.That(() => _queue.Handle(response, 100), Throws.TypeOf<SubprotocolException>());

        response.Received().Dispose();
    }

    [Test]
    public void Handle_does_not_throw_when_completion_source_already_cancelled()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest();

        _queue.Send(request);

        // Simulate timeout cancelling the CompletionSource
        request.CompletionSource.TrySetCanceled();

        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        // Should not throw — this is the core regression test
        Assert.That(() => _queue.Handle(response, 100), Throws.Nothing);
    }

    [Test]
    public void Handle_disposes_data_when_completion_source_already_cancelled()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest();

        _queue.Send(request);

        // Simulate timeout cancelling the CompletionSource
        request.CompletionSource.TrySetCanceled();

        IOwnedReadOnlyList<BlockHeader> response = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();

        _queue.Handle(response, 100);

        response.Received().Dispose();
    }

    [Test]
    public void Handle_dequeues_next_request_even_when_current_was_cancelled()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request1 = CreateRequest();
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request2 = CreateRequest();

        _queue.Send(request1);
        _queue.Send(request2);

        // Simulate timeout cancelling the first request
        request1.CompletionSource.TrySetCanceled();

        IOwnedReadOnlyList<BlockHeader> response = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();

        _queue.Handle(response, 100);

        // The second request should have been dequeued and sent
        Assert.That(_recordedSends.Count, Is.EqualTo(2));
        Assert.That(request2.CompletionSource.Task.IsCompleted, Is.False);
    }

    [Test]
    public void Send_does_not_send_when_closed()
    {
        _queue.CompleteAdding();

        GetBlockHeadersMessage msg = new();
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request = new(msg);

        _queue.Send(request);

        Assert.That(_recordedSends.Count, Is.EqualTo(0));
    }

    [Test]
    public void CompleteAdding_cancels_current_request()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest();

        _queue.Send(request);

        _queue.CompleteAdding();

        Assert.That(request.CompletionSource.Task.IsCanceled, Is.True);
    }

    [Test]
    public void CompleteAdding_cancels_queued_requests()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request1 = CreateRequest();
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request2 = CreateRequest();
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request3 = CreateRequest();

        _queue.Send(request1);
        _queue.Send(request2);
        _queue.Send(request3);

        _queue.CompleteAdding();

        Assert.That(request1.CompletionSource.Task.IsCanceled, Is.True);
        Assert.That(request2.CompletionSource.Task.IsCanceled, Is.True);
        Assert.That(request3.CompletionSource.Task.IsCanceled, Is.True);
    }

    [Test]
    public void Send_after_CompleteAdding_cancels_request()
    {
        _queue.CompleteAdding();

        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest();

        _queue.Send(request);

        Assert.That(request.CompletionSource.Task.IsCanceled, Is.True);
        Assert.That(_recordedSends.Count, Is.EqualTo(0));
    }

    [Test]
    public void Handle_disposes_tuple_disposable_component_when_no_current_request()
    {
        MessageQueue<GetBlockHeadersMessage, (IDisposable, long)> queue = new(RecordingProtocolHandler.Create<GetBlockHeadersMessage>());
        IDisposable inner = Substitute.For<IDisposable>();

        Assert.That(() => queue.Handle((inner, 100L), 100), Throws.TypeOf<SubprotocolException>());

        inner.Received().Dispose();
    }

    private static Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> CreateRequest() => new(new GetBlockHeadersMessage());
}
