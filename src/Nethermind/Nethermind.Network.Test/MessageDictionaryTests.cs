// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using GetBlockHeadersMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockHeadersMessage;

namespace Nethermind.Network.Test;

public class MessageDictionaryTests
{
    private readonly List<Eth66Message<GetBlockHeadersMessage>> _recordedRequests = new();
    private MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>>
        _testMessageDictionary;

    [SetUp]
    public void Setup()
    {
        _recordedRequests.Clear();
        _testMessageDictionary = new(CreateHandler(_recordedRequests));
    }

    [Test]
    public void Test_SendAndReceive()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);

        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        _testMessageDictionary.Send(request);

        _recordedRequests.Count.Should().Be(1);
        _recordedRequests[^1].Should().BeSameAs(request.Message);
        request.CompletionSource.Task.IsCompleted.Should().BeFalse();

        _testMessageDictionary.Handle(111, response, 100);
        request.CompletionSource.Task.IsCompleted.Should().BeTrue();
        request.CompletionSource.Task.Result.Should().BeSameAs(response);
    }

    [Test]
    public void Test_SendAndReceive_withDifferentRequestId()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);

        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        _testMessageDictionary.Send(request);

        _recordedRequests.Count.Should().Be(1);
        _recordedRequests[^1].Should().BeSameAs(request.Message);
        request.CompletionSource.Task.IsCompleted.Should().BeFalse();

        _testMessageDictionary.Invoking((dictionary) => dictionary.Handle(112, response, 100))
            .Should()
            .Throw<SubprotocolException>();
    }

    [Test]
    public void Test_SendAndReceive_outOfOrder()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> requestBefore = CreateRequest(112);
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);

        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();

        _testMessageDictionary.Send(requestBefore);
        _testMessageDictionary.Send(request);

        _recordedRequests.Count.Should().Be(2);
        _recordedRequests[^1].Should().BeSameAs(request.Message);
        request.CompletionSource.Task.IsCompleted.Should().BeFalse();

        _testMessageDictionary.Handle(111, response, 100);
        request.CompletionSource.Task.IsCompleted.Should().BeTrue();
        request.CompletionSource.Task.Result.Should().BeSameAs(response);

        requestBefore.CompletionSource.Task.IsCompleted.Should().BeFalse();
    }

    [Test]
    public void Test_Send_withTooManyConcurrentRequest()
    {
        for (int i = 0; i < 32; i++)
        {
            _testMessageDictionary.Send(CreateRequest(i));
        }

        _testMessageDictionary.Invoking(static (dictionary) => dictionary.Send(CreateRequest(33)))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Test]
    public void Test_Send_MessageDisposing_OnInvalidId()
    {
        IOwnedReadOnlyList<BlockHeader> response = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();
        _testMessageDictionary.Invoking((dictionary) => dictionary.Handle(1234, response, 100))
            .Should()
            .Throw<SubprotocolException>();

        response.Received().Dispose();
    }

    [Test]
    public void Handle_disposes_tuple_disposable_component_on_unmatched_request()
    {
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, (IDisposable, long)> dictionary = new(CreateHandler<Eth66Message<GetBlockHeadersMessage>>());
        IDisposable inner = Substitute.For<IDisposable>();

        dictionary.Invoking(d => d.Handle(9999, (inner, 100L), 100))
            .Should()
            .Throw<SubprotocolException>();

        inner.Received().Dispose();
    }

    private static Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> CreateRequest(int requestId)
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request =
            new(new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(requestId,
                new GetBlockHeadersMessage()));
        return request;
    }

    private static TestProtocolHandler CreateHandler<TMessage>(List<TMessage>? recordedSends = null)
        where TMessage : P2PMessage
    {
        ISession session = Substitute.For<ISession>();
        session.When(s => s.DeliverMessage(Arg.Any<TMessage>()))
            .Do(c => recordedSends?.Add(c.Arg<TMessage>()));
        return new TestProtocolHandler(session);
    }

    private sealed class TestProtocolHandler(ISession session)
        : ProtocolHandlerBase(
            session,
            Substitute.For<INodeStatsManager>(),
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<IBackgroundTaskScheduler>(),
            LimboLogs.Instance)
    {
        public override string Name => "test";
        protected override TimeSpan InitTimeout => TimeSpan.Zero;
        public override byte ProtocolVersion => 0;
        public override string ProtocolCode => "test";
        public override int MessageIdSpaceSize => 0;
        public override void Init() { }
        public override void HandleMessage(Packet message) { }
        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) { }
        public override void Dispose() { }
    }
}
