// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Test.P2P.Subprotocols;
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
    public async Task Test_Send_delivers_before_zero_threshold_cleanup_can_dispose_request()
    {
        bool sentUndisposed = false;
        ISession session = Substitute.For<ISession>();
        session.When(s => s.DeliverMessage(Arg.Any<Eth66Message<GetBlockHeadersMessage>>()))
            .Do(callInfo => sentUndisposed = !((TrackingHeadersMessage)callInfo[0]).IsDisposed);
        RecordingProtocolHandler handler = new(session);
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> dictionary =
            new(handler, TimeSpan.Zero);
        TrackingHeadersMessage message = new(111, new GetBlockHeadersMessage());
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = new(message);

        dictionary.Send(request);

        Assert.That(sentUndisposed, Is.True);

        Exception? completionException = null;
        try
        {
            await request.CompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            completionException = exception;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completionException, Is.TypeOf<TimeoutException>());
            Assert.That(message.IsDisposed, Is.True);
        }
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

    [Test]
    public void ThrowIfNotRequested_keeps_a_pending_request_pending()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);
        using IOwnedReadOnlyList<BlockHeader> response = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();
        _testMessageDictionary.Send(request);

        _testMessageDictionary.ThrowIfNotRequested(UndecodableResponse.Create(111));
        _testMessageDictionary.Handle(111, response, 100);

        Assert.That(request.CompletionSource.Task.Result, Is.SameAs(response));
    }

    [TestCaseSource(nameof(UncorrelatedContents))]
    public void ThrowIfNotRequested_rejects_uncorrelated_content(IByteBuffer content)
    {
        _testMessageDictionary.Send(CreateRequest(111));

        Assert.That(() => _testMessageDictionary.ThrowIfNotRequested(content), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void Expired_response_is_consumed_before_deserialization_and_only_once()
    {
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> dictionary = CreateExpiringDictionary();
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);
        dictionary.Send(request);
        Assert.That(SpinWait.SpinUntil(() => request.Elapsed > TimeSpan.Zero, 1000), Is.True);
        Assert.That(dictionary.TryExpireRequest(111), Is.True);

        using (DisposableByteBuffer content = UndecodableResponse.Create(111).AsDisposable())
        {
            using MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>>.ResponseScope response =
                dictionary.BeginResponse(content);
            Assert.That(response.IsExpired, Is.True);
            Assert.That(response.RequestId, Is.EqualTo(111));
        }

        using (DisposableByteBuffer repeatedContent = UndecodableResponse.Create(111).AsDisposable())
        {
            Assert.That(() => dictionary.BeginResponse(repeatedContent), Throws.TypeOf<SubprotocolException>());
        }
        Assert.That(request.CompletionSource.Task.IsFaulted, Is.True);
    }

    [Test]
    public void Expired_response_handle_disposes_owned_data_and_does_not_throw()
    {
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, (IDisposable, long)> dictionary = new(
            RecordingProtocolHandler.Create<Eth66Message<GetBlockHeadersMessage>>(),
            TimeSpan.Zero,
            new CancellationToken(canceled: true));
        IDisposable inner = Substitute.For<IDisposable>();
        Request<Eth66Message<GetBlockHeadersMessage>, (IDisposable, long)> request =
            new(new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(111, new GetBlockHeadersMessage()));

        dictionary.Send(request);
        Assert.That(SpinWait.SpinUntil(() => request.Elapsed > TimeSpan.Zero, 1000), Is.True);
        Assert.That(dictionary.TryExpireRequest(111), Is.True);
        Assert.DoesNotThrow(() => dictionary.Handle(111, (inner, 100L), 100));
        inner.Received(1).Dispose();
    }

    [Test]
    public void Claimed_response_does_not_remove_reused_request_id()
    {
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> dictionary = CreateExpiringDictionary();
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> claimed = CreateRequest(111);
        dictionary.Send(claimed);
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> replacement = CreateRequest(111);
        using (DisposableByteBuffer content = UndecodableResponse.Create(111).AsDisposable())
        {
            using MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>>.ResponseScope response =
                dictionary.BeginResponse(content);
            Task<bool> expireTask = Task.Run(() => dictionary.TryExpireRequest(111));
            Assert.That(expireTask.Wait(1000), Is.True);
            Assert.That(expireTask.Result, Is.False);
            Task sendTask = Task.Run(() => dictionary.Send(replacement));
            Assert.That(sendTask.Wait(1000), Is.True);

            try
            {
                response.Complete(112, new[] { Build.A.BlockHeader.TestObject }.ToPooledList(), 100);
                Assert.Fail("A completion with a different request id should be rejected");
            }
            catch (SubprotocolException)
            {
            }
        }

        using IOwnedReadOnlyList<BlockHeader> responseData = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();
        dictionary.Handle(111, responseData, 100);
        Assert.That(claimed.CompletionSource.Task.IsFaulted, Is.True);
        Assert.That(replacement.CompletionSource.Task.Result, Is.SameAs(responseData));
    }

    [Test]
    public void Expired_response_cache_is_bounded_and_evicts_oldest_id()
    {
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> dictionary = CreateExpiringDictionary();

        for (int id = 1; id <= 129; id++)
        {
            Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(id);
            dictionary.Send(request);
            Assert.That(SpinWait.SpinUntil(() => request.Elapsed > TimeSpan.Zero, 1000), Is.True);
            Assert.That(dictionary.TryExpireRequest(id), Is.True);
        }

        IOwnedReadOnlyList<BlockHeader> evictedResponse = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();
        Assert.That(() => dictionary.Handle(1, evictedResponse, 100), Throws.TypeOf<SubprotocolException>());
        evictedResponse.Received(1).Dispose();

        IOwnedReadOnlyList<BlockHeader> cachedResponse = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();
        Assert.DoesNotThrow(() => dictionary.Handle(129, cachedResponse, 100));
        cachedResponse.Received(1).Dispose();
    }

    [Test]
    public void Successful_response_does_not_create_an_expired_response_marker()
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);
        using IOwnedReadOnlyList<BlockHeader> responseData = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();
        _testMessageDictionary.Send(request);
        _testMessageDictionary.Handle(111, responseData, 100);

        using DisposableByteBuffer content = UndecodableResponse.Create(111).AsDisposable();
        Assert.That(() => _testMessageDictionary.BeginResponse(content), Throws.TypeOf<SubprotocolException>());
    }

    [Test]
    public void Response_scope_releases_gate_before_completion()
    {
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> dictionary = CreateExpiringDictionary();
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = CreateRequest(111);
        dictionary.Send(request);
        using DisposableByteBuffer content = UndecodableResponse.Create(111).AsDisposable();

        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> replacement = CreateRequest(112);
        IOwnedReadOnlyList<BlockHeader> rejectedData = Substitute.For<IOwnedReadOnlyList<BlockHeader>>();
        using (MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>>.ResponseScope response =
               dictionary.BeginResponse(content))
        {
            Task sendTask = Task.Run(() => dictionary.Send(replacement));
            Assert.That(sendTask.Wait(1000), Is.True);
            try
            {
                response.Complete(112, rejectedData, 100);
                Assert.Fail("A completion with a different request id should be rejected");
            }
            catch (SubprotocolException)
            {
            }
        }

        rejectedData.Received(1).Dispose();
        using IOwnedReadOnlyList<BlockHeader> responseData = new[] { Build.A.BlockHeader.TestObject }.ToPooledList();
        dictionary.Handle(112, responseData, 100);
        Assert.That(replacement.CompletionSource.Task.Result, Is.SameAs(responseData));
        Assert.That(request.CompletionSource.Task.IsFaulted, Is.True);
    }

    [Test]
    public void Aborted_response_scope_cancels_claimed_request()
    {
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> dictionary = CreateExpiringDictionary();
        TrackingHeadersMessage message = new(111, new GetBlockHeadersMessage());
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request = new(message);
        dictionary.Send(request);

        using DisposableByteBuffer content = UndecodableResponse.Create(111).AsDisposable();
        Assert.Throws<InvalidOperationException>(() =>
        {
            using MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>>.ResponseScope response =
                dictionary.BeginResponse(content);
            throw new InvalidOperationException("decoder failed");
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.CompletionSource.Task.IsCanceled, Is.True);
            Assert.That(message.IsDisposed, Is.True);
        }
    }

    [Test]
    public async Task Public_late_response_after_cleanup_expiry_is_ignored_and_disposed()
    {
        MessageDictionary<Eth66Message<GetBlockHeadersMessage>, (IDisposable, long)> dictionary = new(
            RecordingProtocolHandler.Create<Eth66Message<GetBlockHeadersMessage>>(),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Request<Eth66Message<GetBlockHeadersMessage>, (IDisposable, long)> request =
            new(new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(111, new GetBlockHeadersMessage()));
        dictionary.Send(request);

        Exception? completionException = null;
        try
        {
            await request.CompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            completionException = exception;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completionException, Is.TypeOf<TimeoutException>());
            Assert.That(request.CompletionSource.Task.IsCompleted, Is.True);
        }

        IDisposable inner = Substitute.For<IDisposable>();
        Assert.DoesNotThrow(() => dictionary.Handle(111, (inner, 100L), 100));
        inner.Received(1).Dispose();
    }

    private static IEnumerable<TestCaseData> UncorrelatedContents()
    {
        yield return new TestCaseData(UndecodableResponse.Create(112)).SetName("Not pending request id");
        yield return new TestCaseData(Unpooled.WrappedBuffer([0x80])).SetName("Unreadable request id");
        yield return new TestCaseData(Unpooled.WrappedBuffer(Array.Empty<byte>())).SetName("Empty payload");
        yield return new TestCaseData(Unpooled.WrappedBuffer([0xc1])).SetName("List shorter than its header");
        yield return new TestCaseData(Unpooled.WrappedBuffer([0xc2, 0x82, 0x01])).SetName("Request id shorter than its header");
    }

    private static MessageDictionary<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> CreateExpiringDictionary() =>
        new(RecordingProtocolHandler.Create<Eth66Message<GetBlockHeadersMessage>>(), TimeSpan.Zero, new CancellationToken(canceled: true));

    private static Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> CreateRequest(int requestId)
    {
        Request<Eth66Message<GetBlockHeadersMessage>, IOwnedReadOnlyList<BlockHeader>> request =
            new(new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(requestId,
                new GetBlockHeadersMessage()));
        return request;
    }

    private sealed class TrackingHeadersMessage(long requestId, GetBlockHeadersMessage ethMessage) :
        Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(requestId, ethMessage)
    {
        public bool IsDisposed { get; private set; }

        public override void Dispose()
        {
            IsDisposed = true;
            base.Dispose();
        }
    }
}
