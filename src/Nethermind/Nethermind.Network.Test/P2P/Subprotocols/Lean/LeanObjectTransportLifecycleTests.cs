// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Lean;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

public class LeanObjectTransportLifecycleTests
{
    [Test]
    public async Task Disposal_releases_queued_bodies_and_preserves_the_active_owner()
    {
        using LeanTestNode node = new();
        Transaction first = FrameTransaction(93), second = FrameTransaction(94);
        node.Pending.AddRange([first, second]);
        byte[] firstBody = Wrapper(1024, true, first), secondBody = Wrapper(2048, true, second);
        LeanDescriptor firstDescriptor = Describe(firstBody), secondDescriptor = Describe(secondBody);
        FakeLink link = new("disposal");
        LeanPeer peer = node.Connect(link);
        using ManualResetEventSlim gate = new();
        node.Verifier.Gate = gate;
        try
        {
            node.Transport.OnAnnounce(peer, new([firstDescriptor]));
            Serve(node, peer, link, firstDescriptor, firstBody);
            await node.Verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long activeBytes = node.Transport.CompletionBytes;
            node.Transport.OnAnnounce(peer, new([secondDescriptor]));
            Serve(node, peer, link, secondDescriptor, secondBody);
            Assert.That(node.Transport.CompletionBytes, Is.GreaterThan(activeBytes), "the second body is queued behind the blocked verifier");

            node.Transport.Dispose();
            await Until(() => node.Transport.CompletionBytes == activeBytes);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.Transport.IncompleteBytes, Is.Zero, "queued metadata is released");
                Assert.That(node.Transport.CompletionBytes, Is.EqualTo(activeBytes),
                    "queued bodies are released; active verification remains charged");
            }
        }
        finally
        {
            gate.Set();
            await Until(() => node.Transport.CompletionBytes == 0);
        }
        Assert.That(peer.ChargedBytes, Is.Zero);
    }

    [Test]
    public async Task Recovery_overlaps_with_four_workers_while_verification_stays_serialized()
    {
        using LeanTestNode node = new();
        Transaction[] transactions = [.. Enumerable.Range(130, 5).Select(FrameTransaction)];
        byte[][] bodies = [.. transactions.Select((transaction, index) => Wrapper(4096 + index, true, transaction))];
        LeanDescriptor[] descriptors = [.. bodies.Select(Describe)];
        TaskCompletionSource fourRecovering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        FakeLink link = new("overlapping-recovery")
        {
            OnRecoverAsync = async (hashes, token) =>
            {
                if (Interlocked.Increment(ref active) == 4) fourRecovering.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(token);
                    return transactions.Where(transaction => hashes.Contains(transaction.Hash!.ValueHash256)).ToArray();
                }
                finally { Interlocked.Decrement(ref active); }
            }
        };
        LeanPeer peer = node.Connect(link);
        try
        {
            node.Transport.OnAnnounce(peer, new(descriptors));
            for (int index = 0; index < bodies.Length; index++) Serve(node, peer, link, descriptors[index], bodies[index]);
            await fourRecovering.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Volatile.Read(ref active), Is.EqualTo(4), "four ETH requests are simultaneously awaiting replies");
                Assert.That(link.RecoveryCalls, Is.EqualTo(4), "the fifth wrapper stays in the bounded queue");
                Assert.That(node.Verifier.Calls, Is.EqualTo(4));
                Assert.That(node.Verifier.MaxActive, Is.EqualTo(1));
            }

            release.TrySetResult();
            await Until(() => descriptors.All(descriptor => node.Transport.IsStored(descriptor.ObjectId))
                && node.Transport.CompletionBytes == 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(link.RecoveryCalls, Is.EqualTo(5));
                Assert.That(node.Verifier.Calls, Is.EqualTo(5), "verified claims are reused during full admission");
                Assert.That(node.Verifier.MaxActive, Is.EqualTo(1));
                Assert.That(node.Transport.IncompleteBytes, Is.Zero);
                Assert.That(peer.ChargedBytes, Is.Zero);
                Assert.That(link.Penalties, Is.Empty);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task Cancellation_releases_recovering_workers_and_queued_bodies([Values("dispose", "expire", "disconnect")] string stop)
    {
        using LeanTestNode node = new();
        TaskCompletionSource<Transaction[]> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLink link = new("cancel-recovery")
        {
            OnRecoverAsync = (_, token) => reply.Task.WaitAsync(token)
        };
        Transaction[] transactions = [.. Enumerable.Range(140, 5).Select(FrameTransaction)];
        byte[][] bodies = [.. transactions.Select((transaction, index) => Wrapper(4096 + index, true, transaction))];
        LeanDescriptor[] descriptors = [.. bodies.Select(Describe)];
        LeanPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new(descriptors));
        for (int index = 0; index < bodies.Length; index++) Serve(node, peer, link, descriptors[index], bodies[index]);
        await Until(() => link.RecoveryCalls == 4);
        if (stop == "dispose") node.Transport.Dispose();
        else if (stop == "expire") node.Tick(LeanLimits.AssemblyIdle);
        else
        {
            node.Transport.Remove(peer);
            reply.TrySetResult([]); // ETH disconnect completes outstanding recoveries without envelopes.
        }
        await Until(() => node.Transport.CompletionBytes == 0 && node.Transport.AssemblyCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IncompleteBytes, Is.Zero);
            Assert.That(peer.ChargedBytes, Is.Zero);
            Assert.That(link.RecoveryCalls, Is.EqualTo(4));
            Assert.That(descriptors.Any(descriptor => node.Transport.IsStored(descriptor.ObjectId)), Is.False);
            Assert.That(link.Penalties, Is.Empty);
        }
        if (stop != "dispose")
        {
            Transaction transaction = FrameTransaction(190);
            FakeLink healthy = new("recovery-after-cancel") { OnRecover = _ => [transaction] };
            LeanPeer source = node.Connect(healthy);
            byte[] body = Wrapper(4096, true, transaction);
            LeanDescriptor descriptor = Describe(body);
            node.Transport.OnAnnounce(source, new([descriptor]));
            Serve(node, source, healthy, descriptor, body);
            await Until(() => node.Transport.IsStored(descriptor.ObjectId) && node.Transport.CompletionBytes == 0);
            Assert.That(healthy.Penalties, Is.Empty, "workers and the verification gate remain usable after cancellation");
        }
    }

    [Test]
    public async Task Canceled_gate_waiters_release_ownership_without_starting_another_verification([Values] bool dispose)
    {
        using LeanTestNode node = new();
        using ManualResetEventSlim gate = new();
        node.Verifier.Gate = gate;
        Transaction[] transactions = [.. Enumerable.Range(150, 6).Select(FrameTransaction)];
        node.Pending.AddRange(transactions);
        byte[][] bodies = [.. transactions.Select((transaction, index) => Wrapper(4096 + index, true, transaction))];
        LeanDescriptor[] descriptors = [.. bodies.Select(Describe)];
        FakeLink link = new("cancel-verification");
        LeanPeer peer = node.Connect(link);
        try
        {
            node.Transport.OnAnnounce(peer, new(descriptors));
            Serve(node, peer, link, descriptors[0], bodies[0]);
            await node.Verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long activeBytes = node.Transport.CompletionBytes;
            for (int index = 1; index < bodies.Length; index++) Serve(node, peer, link, descriptors[index], bodies[index]);
            await Until(() => node.Transport.QueuedValidations == 2);
            if (dispose) node.Transport.Dispose();
            else node.Tick(LeanLimits.AssemblyIdle);
            await Until(() => node.Transport.CompletionBytes == activeBytes);
            Assert.That(node.Verifier.Calls, Is.EqualTo(1), "canceled semaphore waiters cannot enter the backend");
            gate.Set();
            await Until(() => node.Transport.CompletionBytes == 0 && node.Transport.AssemblyCount == 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.Verifier.Calls, Is.EqualTo(1));
                Assert.That(node.Verifier.MaxActive, Is.EqualTo(1));
                Assert.That(node.Transport.IncompleteBytes, Is.Zero);
                Assert.That(peer.ChargedBytes, Is.Zero);
                Assert.That(link.RecoveryCalls, Is.Zero);
                Assert.That(descriptors.Any(descriptor => node.Transport.IsStored(descriptor.ObjectId)), Is.False);
                Assert.That(link.Penalties, Is.Empty);
            }
        }
        finally { gate.Set(); }
    }

    [Test]
    public async Task Recovery_timeout_is_a_local_failure_and_a_closed_source_does_not_tombstone_the_object()
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        logger.IsInfo.Returns(true);
        using LeanTestNode node = new(logManager: new OneLoggerLogManager(new(logger)));
        TaskCompletionSource recovering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        async Task<Transaction[]> HoldReply(ValueHash256[] _, CancellationToken token)
        {
            if (Interlocked.Increment(ref entered) == 2) recovering.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        }
        Transaction closedTransaction = FrameTransaction(191), timedOutTransaction = FrameTransaction(192);
        byte[] closedBody = Wrapper(4096, true, closedTransaction), timedOutBody = Wrapper(4097, true, timedOutTransaction);
        LeanDescriptor closedDescriptor = Describe(closedBody), timedOutDescriptor = Describe(timedOutBody);
        FakeLink closedLink = new("independent-ETH") { OnRecoverAsync = HoldReply };
        FakeLink timedOutLink = new("silent-ETH") { OnRecoverAsync = HoldReply };
        LeanPeer closedPeer = node.Connect(closedLink), timedOutPeer = node.Connect(timedOutLink);
        node.Transport.OnAnnounce(closedPeer, new([closedDescriptor]));
        node.Transport.OnAnnounce(timedOutPeer, new([timedOutDescriptor]));
        Serve(node, closedPeer, closedLink, closedDescriptor, closedBody);
        Serve(node, timedOutPeer, timedOutLink, timedOutDescriptor, timedOutBody);
        await recovering.Task.WaitAsync(TimeSpan.FromSeconds(10));

        node.Transport.Remove(closedPeer);
        Assert.That(node.Transport.CompletionBytes, Is.GreaterThan(0), "closing QUIC does not complete its independent ETH request");
        await Until(() => node.Transport.AssemblyCount == 0 && node.Transport.CompletionBytes == 0, TimeSpan.FromSeconds(25));
        node.Tick(TimeSpan.FromSeconds(30));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsTombstoned(closedDescriptor.ObjectId), Is.False);
            Assert.That(node.Transport.IsTombstoned(timedOutDescriptor.ObjectId), Is.True, "an open source's unresolved wrapper uses normal local-failure retry policy");
            Assert.That(closedPeer.ChargedBytes, Is.Zero);
            Assert.That(timedOutPeer.ChargedBytes, Is.Zero);
            Assert.That(closedLink.Penalties, Is.Empty);
            Assert.That(timedOutLink.Penalties, Is.Empty);
        }
        logger.DidNotReceive().Warn(Arg.Any<string>());
        logger.Received().Info(Arg.Is<string>(message => message.Contains("local failures 2, expired 0")));

        FakeLink healthy = new("retry-independent-ETH") { OnRecover = _ => [closedTransaction] };
        LeanPeer source = node.Connect(healthy);
        node.Transport.OnAnnounce(source, new([closedDescriptor]));
        Serve(node, source, healthy, closedDescriptor, closedBody);
        await Until(() => node.Transport.IsStored(closedDescriptor.ObjectId) && node.Transport.CompletionBytes == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(healthy.Penalties, Is.Empty);
            Assert.That(node.Verifier.Calls, Is.EqualTo(2), "recovery retries reuse both previously verified statements");
        }
    }

    [Test]
    public async Task Lost_recovery_link_stops_batches_and_late_reservations_without_tombstoning([Values] bool loseEth)
    {
        using LeanTestNode node = new();
        Transaction[] transactions = [.. Enumerable.Range(200, 17).Select(FrameTransaction)];
        byte[] body = Wrapper(4096, true, transactions);
        LeanDescriptor descriptor = Describe(body);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            TaskCompletionSource recovering = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeLink link = new("recovery-reconnect")
            {
                OnRecoverAsync = async (hashes, token) =>
                {
                    recovering.TrySetResult();
                    await reply.Task.WaitAsync(token);
                    return transactions.Where(transaction => hashes.Contains(transaction.Hash!.ValueHash256)).ToArray();
                }
            };
            LeanPeer peer = node.Connect(link);
            node.Transport.OnAnnounce(peer, new([descriptor]));
            Serve(node, peer, link, descriptor, body);
            await recovering.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (loseEth) link.HasTransactionRecovery = false;
            else node.Transport.Remove(peer);
            reply.TrySetResult();
            await Until(() => node.Transport.AssemblyCount == 0 && node.Transport.CompletionBytes == 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.Closed, Is.EqualTo(!loseEth), "the ETH-loss case leaves QUIC open");
                Assert.That(link.RecoveryCalls, Is.EqualTo(1), "the second batch cannot use the lost source");
                Assert.That(link.RecoveryBytes, Is.Zero, "a late response cannot reserve or decode envelopes");
                Assert.That(node.Transport.IsTombstoned(descriptor.ObjectId), Is.False);
                Assert.That(peer.ChargedBytes, Is.Zero);
                Assert.That(node.Transport.IncompleteBytes, Is.Zero);
                Assert.That(link.Penalties, Is.Empty);
                Assert.That(node.Verifier.Calls, Is.EqualTo(1), "repeated refusals do not repeat native claims verification");
            }
            node.Transport.Remove(peer);
        }
        FakeLink healthy = new("recovery-reconnect")
        {
            OnRecover = hashes => transactions.Where(transaction => hashes.Contains(transaction.Hash!.ValueHash256)).ToArray()
        };
        LeanPeer source = node.Connect(healthy);
        node.Transport.OnAnnounce(source, new([descriptor]));
        Serve(node, source, healthy, descriptor, body);
        await Until(() => node.Transport.IsStored(descriptor.ObjectId) && node.Transport.CompletionBytes == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(healthy.RecoveryCalls, Is.EqualTo(2), "healthy recovery still obtains all seventeen envelopes");
            Assert.That(node.Verifier.Calls, Is.EqualTo(1));
            Assert.That(source.ChargedBytes, Is.Zero);
            Assert.That(healthy.Penalties, Is.Empty);
        }
    }

    [Test]
    public async Task Locally_resolved_wrapper_still_validates_after_ETH_link_loss()
    {
        using LeanTestNode node = new();
        Transaction transaction = FrameTransaction(218);
        node.Pending.Add(transaction);
        byte[] body = Wrapper(4096, true, transaction);
        LeanDescriptor descriptor = Describe(body);
        FakeLink link = new("local-envelope");
        LeanPeer peer = node.Connect(link);
        link.HasTransactionRecovery = false;
        node.Transport.OnAnnounce(peer, new([descriptor]));
        Serve(node, peer, link, descriptor, body);
        await Until(() => node.Transport.IsStored(descriptor.ObjectId) && node.Transport.CompletionBytes == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.RecoveryCalls, Is.Zero);
            Assert.That(node.Verifier.Calls, Is.EqualTo(1));
            Assert.That(peer.ChargedBytes, Is.Zero);
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    [Test]
    public async Task ETH_loss_after_reservation_only_refuses_incomplete_recovery([Values] bool complete)
    {
        using LeanTestNode node = new();
        Transaction[] transactions = [.. Enumerable.Range(220, 17).Select(FrameTransaction)];
        byte[] body = Wrapper(4096, true, transactions);
        LeanDescriptor descriptor = Describe(body);
        FakeLink link = new("reserved-envelopes")
        {
            OnRecover = hashes => transactions.Where(transaction => hashes.Contains(transaction.Hash!.ValueHash256)).ToArray()
        };
        int responses = 0;
        link.OnRecoveryReserved = () =>
        {
            if (++responses == (complete ? 2 : 1)) link.HasTransactionRecovery = false;
        };
        LeanPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([descriptor]));
        Serve(node, peer, link, descriptor, body);
        await Until(() => node.Transport.AssemblyCount == 0 && node.Transport.CompletionBytes == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.HasTransactionRecovery, Is.False);
            Assert.That(link.RecoveryCalls, Is.EqualTo(complete ? 2 : 1));
            Assert.That(node.Transport.IsStored(descriptor.ObjectId), Is.EqualTo(complete));
            Assert.That(node.Transport.IsTombstoned(descriptor.ObjectId), Is.False);
            Assert.That(node.Verifier.Calls, Is.EqualTo(1));
            Assert.That(peer.ChargedBytes, Is.Zero);
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    [Test]
    public async Task Non_recovery_local_failure_keeps_backoff_after_ETH_link_loss()
    {
        using LeanTestNode node = new();
        Transaction transaction = FrameTransaction(238);
        node.Pending.Add(transaction);
        node.Verifier.VerificationError = new ArgumentException("local verifier unavailable");
        byte[] body = Wrapper(4096, true, transaction);
        LeanDescriptor descriptor = Describe(body);
        FakeLink link = new("local-failure-without-ETH");
        LeanPeer peer = node.Connect(link);
        link.HasTransactionRecovery = false;
        node.Transport.OnAnnounce(peer, new([descriptor]));
        Serve(node, peer, link, descriptor, body);
        await Until(() => node.Transport.AssemblyCount == 0 && node.Transport.CompletionBytes == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsTombstoned(descriptor.ObjectId), Is.True, "an unrelated local failure retains its backoff");
            Assert.That(node.Transport.IsStored(descriptor.ObjectId), Is.False);
            Assert.That(link.RecoveryCalls, Is.Zero);
            Assert.That(node.Verifier.Calls, Is.EqualTo(1));
            Assert.That(peer.ChargedBytes, Is.Zero);
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    private static void Serve(LeanTestNode node, LeanPeer peer, FakeLink link, LeanDescriptor descriptor, byte[] body)
    {
        GetChunksMessage request = link.Sent<GetChunksMessage>().Single(request => request.ObjectId == descriptor.ObjectId);
        foreach (int index in request.Indices)
            node.Transport.OnChunk(peer, LeanChunkView.Parse(Wire(Chunk(request.RequestId, descriptor, body, index))));
        node.Transport.OnComplete(peer, new(request.RequestId, descriptor.ObjectId, LeanCompleteStatus.Served));
    }

    private static async Task Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        using CancellationTokenSource cancellation = new(timeout ?? TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, cancellation.Token);
    }
}
