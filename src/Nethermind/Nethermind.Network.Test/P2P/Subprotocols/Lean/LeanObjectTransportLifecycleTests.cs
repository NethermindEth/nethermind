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
