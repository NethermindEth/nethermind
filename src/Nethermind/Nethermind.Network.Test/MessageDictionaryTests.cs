// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using NUnit.Framework;
using GetBlockHeadersMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockHeadersMessage;

namespace Nethermind.Network.Test;

public class MessageDictionaryTests
{
    private readonly List<Eth66Message<GetBlockHeadersMessage>> _recordedRequests = new();
    private MessageDictionary<Eth66Message<GetBlockHeadersMessage>, GetBlockHeadersMessage, BlockHeader[]>
        _testMessageDictionary;

    [SetUp]
    public void Setup()
    {
        _recordedRequests.Clear();
        _testMessageDictionary = new((message) => _recordedRequests.Add(message));
    }

    [Test]
    public void Test_SendAndReceive()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, BlockHeader[]> request = CreateRequest(111);

        BlockHeader[] response = new[] { Build.A.BlockHeader.TestObject };

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
        Request<Eth66Message<GetBlockHeadersMessage>, BlockHeader[]> request = CreateRequest(111);

        BlockHeader[] response = new[] { Build.A.BlockHeader.TestObject };

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
        Request<Eth66Message<GetBlockHeadersMessage>, BlockHeader[]> requestBefore = CreateRequest(112);
        Request<Eth66Message<GetBlockHeadersMessage>, BlockHeader[]> request = CreateRequest(111);

        BlockHeader[] response = new[] { Build.A.BlockHeader.TestObject };

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

        _testMessageDictionary.Invoking((dictionary) => dictionary.Send(CreateRequest(33)))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Test]
    public async Task Test_OldRequest_WillThrowWithTimeout()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(100);
        Request<Eth66Message<GetBlockHeadersMessage>, BlockHeader[]> request = CreateRequest(0);
        BlockHeader[] response = new[] { Build.A.BlockHeader.TestObject };

        _testMessageDictionary = new((message) => _recordedRequests.Add(message), timeout);

        request.CompletionSource.Task.IsCompleted.Should().BeFalse();
        request.CompletionSource.Task.IsFaulted.Should().BeFalse();

        _testMessageDictionary.Send(request);

        await Task.Delay(timeout * 2);

        request.CompletionSource.Task.IsFaulted.Should().BeTrue();
        request.CompletionSource.Task.Exception.InnerException.Should().BeOfType<TimeoutException>();

        _testMessageDictionary.Invoking((dictionary) => dictionary.Handle(0, response, 100))
            .Should()
            .Throw<SubprotocolException>();
    }

    private static Request<Eth66Message<GetBlockHeadersMessage>, BlockHeader[]> CreateRequest(int requestId)
    {
        Request<Eth66Message<GetBlockHeadersMessage>, BlockHeader[]> request =
            new(new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(requestId,
                new GetBlockHeadersMessage()));
        return request;
    }

}
