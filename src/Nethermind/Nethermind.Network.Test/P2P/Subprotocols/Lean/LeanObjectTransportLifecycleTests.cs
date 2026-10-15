// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Lean;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

public class LeanObjectTransportLifecycleTests
{
    [Test]
    public async Task Disposal_releases_queued_bodies_and_preserves_active_owners([Values] bool retainFetchedBody)
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
        LeanFetchedBody? fetched = null;
        try
        {
            node.Transport.OnAnnounce(peer, new([firstDescriptor]));
            Serve(node, peer, link, firstDescriptor, firstBody);
            await node.Verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long activeBytes = node.Transport.CompletionBytes;
            node.Transport.OnAnnounce(peer, new([secondDescriptor]));
            Serve(node, peer, link, secondDescriptor, secondBody);
            Assert.That(node.Transport.CompletionBytes, Is.GreaterThan(activeBytes), "the second body is queued behind the blocked verifier");
            if (retainFetchedBody)
            {
                byte[] proofBody = LeanBodies.EncodeBlockProof(Proof(3072));
                LeanDescriptor proofDescriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
                    LeanDescriptor.BlockProofContext(TestItem.KeccakA.ValueHash256), proofBody);
                Task<LeanFetchedBody?> waiting = node.Transport.FetchBodyAsync(proofDescriptor, peer, CancellationToken.None);
                Serve(node, peer, link, proofDescriptor, proofBody);
                fetched = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(fetched, Is.Not.Null);
            }

            node.Transport.Dispose();
            await Until(() => node.Transport.CompletionBytes == activeBytes + (fetched?.Body.Length ?? 0));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.Transport.IncompleteBytes, Is.Zero, "queued metadata is released");
                Assert.That(node.Transport.CompletionBytes, Is.EqualTo(activeBytes + (fetched?.Body.Length ?? 0)),
                    "queued bodies are released; active verification and fetched leases remain charged");
            }
            gate.Set();
            await Until(() => node.Transport.CompletionBytes == (fetched?.Body.Length ?? 0));
            Assert.That(peer.ChargedBytes, Is.EqualTo(fetched?.Body.Length ?? 0));
        }
        finally
        {
            gate.Set();
            fetched?.Dispose();
            await Until(() => node.Transport.CompletionBytes == 0);
        }
        Assert.That(peer.ChargedBytes, Is.Zero);
    }

    [Test]
    public async Task Recovery_overlaps_with_four_workers_while_verification_and_waited_import_stay_bounded()
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

            FakeLink healthy = new("waited-import");
            LeanPeer source = node.Connect(healthy);
            byte[] proofBody = LeanBodies.EncodeBlockProof(Proof(3072));
            LeanDescriptor proofDescriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
                LeanDescriptor.BlockProofContext(TestItem.KeccakA.ValueHash256), proofBody);
            Task<LeanFetchedBody?> waiting = node.Transport.FetchBodyAsync(proofDescriptor, source, CancellationToken.None);
            Serve(node, source, healthy, proofDescriptor, proofBody);
            using (LeanFetchedBody? fetched = await waiting.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                Assert.That(fetched, Is.Not.Null, "waited kind 2 retrieval bypasses occupied wrapper workers");
                Assert.That(fetched!.Body, Is.EqualTo(proofBody));
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
        else if (stop == "expire") node.Tick(LeanProtocol.MaxAssemblyIdle);
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
            else node.Tick(LeanProtocol.MaxAssemblyIdle);
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

    private static void Serve(LeanTestNode node, LeanPeer peer, FakeLink link, LeanDescriptor descriptor, byte[] body)
    {
        GetChunksMessage request = link.Sent<GetChunksMessage>().Single(request => request.ObjectId == descriptor.ObjectId);
        foreach (int index in request.Indices)
            node.Transport.OnChunk(peer, LeanChunkView.Parse(Wire(Chunk(request.RequestId, descriptor, body, index))));
        node.Transport.OnComplete(peer, new(request.RequestId, descriptor.ObjectId, LeanCompleteStatus.Served));
    }

    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
