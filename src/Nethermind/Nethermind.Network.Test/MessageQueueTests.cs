// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
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
    private readonly List<GetBlockHeadersMessage> _recordedSends = new();
    private MessageQueue<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> _queue;

    [SetUp]
    public void Setup()
    {
        _recordedSends.Clear();
        _queue = new((message) => _recordedSends.Add(message));
    }

    [Test]
    public void Send_first_request_is_sent_immediately()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest();

        _queue.Send(request);

        _recordedSends.Count.Should().Be(1);
        request.CompletionSource.Task.IsCompleted.Should().BeFalse();
    }

    [Test]
    public void Send_second_request_is_queued()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request1 = CreateRequest();
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request2 = CreateRequest();

        _queue.Send(request1);
        _queue.Send(request2);

        _recordedSends.Count.Should().Be(1);
        request2.CompletionSource.Task.IsCompleted.Should().BeFalse();
    }

    [Test]
    public void Handle_completes_current_request_and_sends_next()
    {
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request1 = CreateRequest();
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request2 = CreateRequest();

        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        _queue.Send(request1);
        _queue.Send(request2);

        _queue.Handle(response, 100);

        request1.CompletionSource.Task.IsCompleted.Should().BeTrue();
        request1.CompletionSource.Task.Result.Should().BeSameAs(response);
        _recordedSends.Count.Should().Be(2);
    }

    [Test]
    public void Handle_throws_when_no_current_request()
    {
        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        _queue.Invoking(q => q.Handle(response, 100))
            .Should()
            .Throw<SubprotocolException>();
    }

    [Test]
    public void Handle_disposes_data_when_no_current_request()
    {
        IOwnedReadOnlyList<BlockHeader> response = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();

        _queue.Invoking(q => q.Handle(response, 100))
            .Should()
            .Throw<SubprotocolException>();

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
        _queue.Invoking(q => q.Handle(response, 100))
            .Should()
            .NotThrow();
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
        _recordedSends.Count.Should().Be(2);
        request2.CompletionSource.Task.IsCompleted.Should().BeFalse();
    }

    [Test]
    public void Send_disposes_message_when_closed()
    {
        _queue.CompleteAdding();

        GetBlockHeadersMessage msg = new();
        Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> request = new(msg);

        _queue.Send(request);

        _recordedSends.Count.Should().Be(0);
    }

    private static Request<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>> CreateRequest()
    {
        return new(new GetBlockHeadersMessage());
    }
}
