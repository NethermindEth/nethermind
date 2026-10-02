// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Nethermind.Libp2p.Protocols.Yamux 1.0.0 can drop the peer's first frames on a channel this node opens, so the channel never reaches its protocol.
/// Such a request is opened once more after a short bound instead of waiting out its budget, the first attempt cancelled before the second starts.
/// </summary>
public class ChannelOpenRetryTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly TimeSpan OpenBound = TimeSpan.FromMilliseconds(200);

    [Test]
    [CancelAfter(30_000)]
    public async Task A_request_whose_first_channel_is_dropped_is_answered_on_a_second_one(CancellationToken token)
    {
        DroppingOpener opener = new(drops: 1);
        await using BeaconP2P node = CreateHost(TimeSpan.FromSeconds(10));
        RequestTiming timing = new();

        IReadOnlyList<ForkedSignedBeaconBlock> blocks = await node.RequestBlocksByRootAsync(opener.Session, [Hash256.Zero], token, timing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks, Is.Empty, "the second channel's answer is the request's");
            Assert.That(opener.Attempts, Is.EqualTo(2));
            Assert.That(opener.EarlierCancelledWhenOpened, Is.EqualTo(new[] { true }), "the dropped attempt is cancelled before the second starts");
            Assert.That(timing.ChannelOpened, Is.True);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_request_whose_second_channel_is_dropped_too_fails_as_a_channel_that_never_opened(CancellationToken token)
    {
        DroppingOpener opener = new(drops: 2);
        await using BeaconP2P node = CreateHost(TimeSpan.FromSeconds(1));

        ReqRespTimeoutException? failure = Assert.ThrowsAsync<ReqRespTimeoutException>(() => node.RequestBlocksByRootAsync(opener.Session, [Hash256.Zero], token, new RequestTiming()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.ChannelNeverOpened, Is.True);
            Assert.That(PeerFailureClassifier.Classify(failure), Is.EqualTo(PeerFailureReason.RequestFailed), "counted against the peer as an unanswered request was before");
            Assert.That(opener.Attempts, Is.EqualTo(2), "one second channel at most, so an unresponsive peer gets no more than twice the opens");
            Assert.That(opener.EarlierCancelledWhenOpened, Is.EqualTo(new[] { true }));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_first_channel_that_reaches_its_protocol_after_it_was_abandoned_does_not_mark_the_second_opened(CancellationToken token)
    {
        DroppingOpener opener = new(drops: 2, openWhenAbandoned: true);
        await using BeaconP2P node = CreateHost(TimeSpan.FromSeconds(1));

        ReqRespTimeoutException? failure = Assert.ThrowsAsync<ReqRespTimeoutException>(() => node.RequestBlocksByRootAsync(opener.Session, [Hash256.Zero], token, new RequestTiming()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opener.LateOpens, Is.EqualTo(1), "fixture: the abandoned channel reached its protocol late");
            Assert.That(failure!.ChannelNeverOpened, Is.True, "the second channel never opened, whatever the first did");
        }
    }

    /// <summary>An attempt is either abandoned or opened, never both, so an abandoned channel that opens late sends nothing and marks nothing.</summary>
    [Test]
    public void An_abandoned_attempt_cannot_open_and_an_opened_one_cannot_be_abandoned()
    {
        RequestTiming timing = new();
        object abandoned = timing.Track(new object());

        Assert.That(timing.TryAbandon(), Is.True);
        Assert.That(() => RequestTiming.Open(abandoned), Throws.InstanceOf<OperationCanceledException>(), "its protocol stops before writing the request");
        Assert.That(timing.ChannelOpened, Is.False);

        object current = timing.Track(new object());
        using RequestTiming.Exchange opened = RequestTiming.Open(current);
        Assert.That((timing.ChannelOpened, timing.TryAbandon()), Is.EqualTo((true, false)), "a channel that reached its protocol is waited for, not replaced");

        IdentifyAgentVersionProbe.Attempt identify = new();
        Assert.That(identify.TryAbandon(), Is.True);
        Assert.That(identify.Open, Throws.InstanceOf<OperationCanceledException>(), "the probe stops before reading");
        IdentifyAgentVersionProbe.Attempt answered = new();
        answered.Open();
        Assert.That(answered.TryAbandon(), Is.False);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_request_its_caller_cancels_before_the_bound_cancels_its_channel(CancellationToken token)
    {
        DroppingOpener opener = new(drops: 1);
        await using BeaconP2P node = CreateHost(TimeSpan.FromSeconds(10));
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        caller.CancelAfter(OpenBound / 4);

        Assert.That(async () => await node.RequestBlocksByRootAsync(opener.Session, [Hash256.Zero], caller.Token, new RequestTiming()), Throws.InstanceOf<OperationCanceledException>());

        Assert.That((opener.Attempts, opener.FirstCancelled), Is.EqualTo((1, true)), "no second attempt, and the first is cancelled with the request");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_request_whose_channel_opened_waits_for_its_answer_past_the_bound(CancellationToken token)
    {
        DroppingOpener opener = new(drops: 0, answerAfter: OpenBound * 3);
        await using BeaconP2P node = CreateHost(TimeSpan.FromSeconds(10));

        IReadOnlyList<ForkedSignedBeaconBlock> blocks = await node.RequestBlocksByRootAsync(opener.Session, [Hash256.Zero], token, new RequestTiming());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks, Is.Empty);
            Assert.That(opener.Attempts, Is.EqualTo(1), "a slow peer whose channel opened is not asked twice");
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    [CancelAfter(30_000)]
    public async Task Identify_whose_first_channel_is_dropped_is_answered_on_a_second_one(int drops, CancellationToken token)
    {
        int attempts = 0;
        ConcurrentQueue<IdentifyAgentVersionProbe.Attempt> seen = new();
        ISession session = Substitute.For<ISession>();
        session.DialAsync<IdentifyAgentVersionProbe, IdentifyAgentVersionProbe.Attempt, string?>(default!, default).ReturnsForAnyArgs(call =>
        {
            IdentifyAgentVersionProbe.Attempt attempt = call.Arg<IdentifyAgentVersionProbe.Attempt>();
            seen.Enqueue(attempt);
            if (Interlocked.Increment(ref attempts) <= drops)
            {
                return Never<string?>(call.Arg<CancellationToken>());
            }

            attempt.Open();
            return Task.FromResult<string?>("agent");
        });
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(OpenBound * 5);

        Task<string?> identify = BeaconP2P.DialIdentifyAsync(session, OpenBound, bound.Token);

        if (drops == 1)
        {
            Assert.That(await identify, Is.EqualTo("agent"));
        }
        else
        {
            Assert.That(async () => await identify, Throws.InstanceOf<OperationCanceledException>(), "a second drop ends at the identify bound as before");
        }

        Assert.That((attempts, seen.Distinct().Count()), Is.EqualTo((2, 2)), "each channel carries its own attempt, so a late open of the first cannot pass for the second");
    }

    private static BeaconP2P CreateHost(TimeSpan requestTimeout) =>
        new(new BeaconChainConfig { P2PPort = 0 }, Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new BeaconChainStatusHolder(Spec, Timestamper.Default),
            new LocalMetadataSource(), new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance)
        { RequestTimeout = requestTimeout, ChannelOpenBound = OpenBound };

    private static async Task<T> Never<T>(CancellationToken token)
    {
        await Task.Delay(System.Threading.Timeout.Infinite, token);
        throw new UnreachableException();
    }

    /// <summary>A session whose first <paramref name="drops"/> block requests never reach the protocol, as when the peer's first frames are dropped.</summary>
    private sealed class DroppingOpener
    {
        private readonly ConcurrentQueue<CancellationToken> _tokens = new();
        private readonly ConcurrentQueue<bool> _earlierCancelled = new();
        private int _attempts;
        private int _lateOpens;

        public DroppingOpener(int drops, TimeSpan answerAfter = default, bool openWhenAbandoned = false)
        {
            Session = Substitute.For<ISession>();
            Session.DialAsync<BeaconBlocksByRootProtocolV2, Hash256[], IReadOnlyList<ForkedSignedBeaconBlock>>(default!, default).ReturnsForAnyArgs(call =>
            {
                CancellationToken token = call.Arg<CancellationToken>();
                if (_tokens.LastOrDefault() is { CanBeCanceled: true } earlier)
                {
                    _earlierCancelled.Enqueue(earlier.IsCancellationRequested);
                }

                _tokens.Enqueue(token);
                Hash256[] request = call.Arg<Hash256[]>();
                int attempt = Interlocked.Increment(ref _attempts);
                return attempt > drops ? AnswerAsync(request, answerAfter, token)
                    : attempt == 1 && openWhenAbandoned ? OpenWhenAbandonedAsync(request, token)
                    : Never<IReadOnlyList<ForkedSignedBeaconBlock>>(token);
            });
        }

        public ISession Session { get; }

        public int Attempts => Volatile.Read(ref _attempts);

        public int LateOpens => Volatile.Read(ref _lateOpens);

        public bool FirstCancelled => _tokens.TryPeek(out CancellationToken first) && first.IsCancellationRequested;

        /// <summary>For each attempt after the first, whether the one before it had been cancelled when it began.</summary>
        public bool[] EarlierCancelledWhenOpened => [.. _earlierCancelled];

        /// <summary>Reaches the protocol only once the request has given up on this channel, as a negotiation that completes late does.</summary>
        private async Task<IReadOnlyList<ForkedSignedBeaconBlock>> OpenWhenAbandonedAsync(Hash256[] request, CancellationToken token)
        {
            try
            {
                return await Never<IReadOnlyList<ForkedSignedBeaconBlock>>(token);
            }
            catch (OperationCanceledException)
            {
                // After the second channel has started, as the late negotiation the request cannot tell apart.
                await Task.Delay(OpenBound / 2, CancellationToken.None);
                Interlocked.Increment(ref _lateOpens);
                using RequestTiming.Exchange exchange = RequestTiming.Open(request);
                throw;
            }
        }

        private static async Task<IReadOnlyList<ForkedSignedBeaconBlock>> AnswerAsync(Hash256[] request, TimeSpan after, CancellationToken token)
        {
            using RequestTiming.Exchange exchange = RequestTiming.Open(request);
            await Task.Delay(after, token);
            return [];
        }
    }
}
