// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Blockchain;
using Nethermind.Consensus.Eip8288;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

public class Lean1ProtocolHandlerTests
{
    private sealed class Scheduler : IBackgroundTaskScheduler
    {
        public Task Completion { get; private set; } = Task.CompletedTask;
        public bool TryScheduleTask<T>(T request, Func<T, CancellationToken, Task> execute, TimeSpan? timeout = null)
            where T : notnull, IBackgroundTaskRequest<T>
        {
            Completion = execute(request, CancellationToken.None);
            return true;
        }
    }

    private sealed class Context : IDisposable
    {
        public ISession Session { get; } = Substitute.For<ISession, ILeanBulkSession>();
        public IBlockTree Tree { get; } = Substitute.For<IBlockTree>();
        public Scheduler Scheduler { get; } = new();
        public LeanReassemblyBudget Budget { get; } = new();
        public Lean1ProtocolHandler Handler { get; }
        private readonly LeanProofGossip _gossip;

        public Context()
        {
            Session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 1000, true));
            Session.HasAgreedCapability(Arg.Any<Capability>()).Returns(true);
            ((ILeanBulkSession)Session).EnableLeanBulk().Returns(true);
            Block genesis = Build.A.Block.WithHeader(Build.A.BlockHeader.WithHash(TestItem.KeccakA).TestObject).TestObject;
            Tree.Genesis.Returns(genesis.Header);
            Tree.Head.Returns(genesis);
            Tree.ChainId.Returns(1UL);
            ITxPool pool = Substitute.For<ITxPool>();
            pool.GetPendingTransactions().Returns([]);
            pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
            ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance),
                Tree, new LeanProofStore(), Substitute.For<ILeanProofVerifier>());
            _gossip = new(service, LimboLogs.Instance);
            MessageSerializationService serializer = new(
                new SerializerInfo(typeof(LeanStatusMessage), new LeanStatusMessageSerializer()),
                new SerializerInfo(typeof(LeanProofChunkMessage), new LeanProofChunkMessageSerializer()));
            Handler = new(Session, Substitute.For<INodeStatsManager>(), serializer, Scheduler,
                LimboLogs.Instance, Tree, service, _gossip, Budget);
        }

        public void Receive<T>(T message, IZeroMessageSerializer<T> serializer) where T : P2PMessage
        {
            IByteBuffer buffer = Unpooled.Buffer();
            try
            {
                serializer.Serialize(buffer, message);
                Handler.HandleMessage(new ZeroPacket(buffer) { PacketType = (byte)message.PacketType, Protocol = "lean" });
            }
            finally { buffer.Release(); }
        }

        public void Handshake()
        {
            int initialized = 0;
            Handler.ProtocolInitialized += (_, _) => initialized++;
            Handler.Init();
            Receive(new LeanStatusMessage(Tree.ChainId, Tree.Genesis.Hash, Eip8288Constants.AggregatedVk.ToArray()), new LeanStatusMessageSerializer());
            Assert.That(initialized, Is.EqualTo(1), "the fixture must establish the negotiated handshake before sending wrappers");
        }

        public void Dispose() { Handler.Dispose(); _gossip.Dispose(); }
    }

    [TestCase("genesis", DisconnectReason.ClientQuitting)]
    [TestCase("capability", DisconnectReason.BreachOfProtocol)]
    [TestCase("bulk", DisconnectReason.BreachOfProtocol)]
    public void Unavailable_initialization_closes_with_an_explicit_reason(string unavailable, DisconnectReason reason)
    {
        using Context context = new();
        if (unavailable == "genesis") context.Tree.Genesis.Returns((BlockHeader)null);
        if (unavailable == "capability") context.Session.HasAgreedCapability(Arg.Any<Capability>()).Returns(false);
        if (unavailable == "bulk") ((ILeanBulkSession)context.Session).EnableLeanBulk().Returns(false);
        context.Handler.Init();
        context.Session.Received(1).InitiateDisconnect(reason, Arg.Any<string>());
        context.Session.DidNotReceive().DeliverMessage(Arg.Any<LeanStatusMessage>());
        Assert.That(context.Budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Negotiated_status_initializes_and_disposal_releases_incomplete_chunks()
    {
        using Context context = new();
        int initialized = 0;
        context.Handler.ProtocolInitialized += (_, _) => initialized++;
        context.Handshake();
        Assert.That(initialized, Is.EqualTo(1));
        byte[] fragment = new byte[LeanProofChunkMessage.DefaultChunkSize];
        context.Receive(new LeanProofChunkMessage(default, fragment.Length * 2, 0, 2, fragment.Length, fragment),
            new LeanProofChunkMessageSerializer());
        Assert.That(context.Budget.RetainedBytes, Is.GreaterThan(0));
        context.Handler.Dispose();
        Assert.That(context.Budget.RetainedBytes, Is.Zero);
        context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public async Task Complete_unknown_hash_wrapper_releases_its_lease_without_peer_penalty()
    {
        using Context context = new();
        context.Handshake();
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(TestItem.KeccakB)],
            Deps = [],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = []
        }).Bytes;
        context.Receive(new LeanProofChunkMessage(ValueKeccak.Compute(wrapper), wrapper.Length, 0, 1,
            LeanProofChunkMessage.DefaultChunkSize, wrapper), new LeanProofChunkMessageSerializer());
        await context.Scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(context.Budget.RetainedBytes, Is.Zero);
        context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }
}
