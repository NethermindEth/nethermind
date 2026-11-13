// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Network;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class SequencerHeadTests
{
    [Test]
    public async Task Peer_head_imports_hash_bound_ancestors_in_order()
    {
        await using PeerSyncFixture fixture = PeerSyncFixture.Create();
        Block[] branch = fixture.Branch(fixture.Heads.Safe, 3, 0x71);
        fixture.Announce(branch, 1);

        await fixture.Advance();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Engine.Inserted.Select(static block => block.Hash), Is.EqualTo(branch.Select(static block => block.Hash)));
            Assert.That(fixture.Heads.Head.Hash, Is.EqualTo(branch[^1].Hash));
            Assert.That(fixture.Heads.Safe.Hash, Is.EqualTo(branch[0].ParentHash));
            Assert.That(fixture.Engine.Forkchoices.All(choice => choice.Safe == fixture.Heads.Safe.Hash), Is.True);
        }
        await fixture.Peer.Received(1).GetHeadBlockHeader(branch[1].Hash, Arg.Any<CancellationToken>());
        await fixture.Peer.Received(2).GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task L1_hold_prevents_peer_fetch_and_engine_changes()
    {
        await using PeerSyncFixture fixture = PeerSyncFixture.Create();
        fixture.Announce(fixture.Branch(fixture.Heads.Safe, 2, 0x72), 1);
        fixture.Heads.UnsafeHeadHeld = true;

        await fixture.Advance();

        Assert.That(fixture.Engine.Inserted, Is.Empty);
        await fixture.Peer.DidNotReceiveWithAnyArgs().GetHeadBlockHeader(default, default);
    }

    [Test]
    public async Task New_signed_counter_replaces_only_the_unsafe_branch()
    {
        await using PeerSyncFixture fixture = PeerSyncFixture.Create();
        Block[] first = fixture.Branch(fixture.Heads.Safe, 2, 0x73);
        fixture.Announce(first, 1);
        await fixture.Advance();
        Block[] second = fixture.Branch(fixture.Heads.Safe, 2, 0x74);
        fixture.Announce(second, 1, 2);
        fixture.Engine.Inserted.Clear();

        await fixture.Advance();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Heads.Head.Hash, Is.EqualTo(second[^1].Hash));
            Assert.That(fixture.Heads.Safe.Hash, Is.EqualTo(first[0].ParentHash));
            Assert.That(fixture.Engine.Inserted.Select(static block => block.Hash), Is.EqualTo(second.Select(static block => block.Hash)));
        }
    }

    [Test]
    public async Task Missing_highest_ancestor_does_not_select_an_older_claim()
    {
        await using PeerSyncFixture fixture = PeerSyncFixture.Create();
        fixture.Announce(fixture.Branch(fixture.Heads.Safe, 1, 0x75), 1);
        Block[] unavailable = fixture.Branch(fixture.Heads.Safe, 2, 0x76);
        fixture.Announce(unavailable, 2, serve: false);

        await fixture.Advance();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Engine.Inserted, Is.Empty);
            Assert.That(fixture.Heads.Head.Hash, Is.EqualTo(fixture.Heads.Safe.Hash));
        }
    }

    [Test]
    public async Task A_claim_conflicting_with_safe_can_fall_back_to_a_compatible_claim()
    {
        await using PeerSyncFixture fixture = PeerSyncFixture.Create();
        Block safe = fixture.Branch(fixture.Heads.Safe, 1, 0x77)[0];
        await fixture.Engine.Insert(safe);
        await fixture.Heads.AdvanceSafe(safe.Header);
        fixture.Engine.Inserted.Clear();
        Block[] compatible = fixture.Branch(safe.Header, 1, 0x78);
        fixture.Announce(compatible, 1);
        fixture.Announce(fixture.Branch(fixture.Tree.Genesis!, 2, 0x79), 2);

        await fixture.Advance();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Heads.Head.Hash, Is.EqualTo(compatible[^1].Hash));
            Assert.That(fixture.Heads.Safe.Hash, Is.EqualTo(safe.Hash));
            Assert.That(fixture.Engine.Inserted.Select(static block => block.Hash), Is.EqualTo(compatible.Select(static block => block.Hash)));
        }
    }

    [Test]
    public async Task Rejected_engine_block_does_not_change_safe_or_try_an_older_claim()
    {
        await using PeerSyncFixture fixture = PeerSyncFixture.Create();
        fixture.Announce(fixture.Branch(fixture.Heads.Safe, 1, 0x7a), 1);
        Block invalid = fixture.Branch(fixture.Heads.Safe, 1, 0x7b)[0];
        fixture.Announce([invalid], 2);
        fixture.Engine.Rejected.Add(invalid.Hash!);

        await fixture.Advance();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Engine.Inserted, Is.Empty);
            Assert.That(fixture.Heads.Head.Hash, Is.EqualTo(fixture.Heads.Safe.Hash));
        }
    }

    [Test]
    public async Task Peer_body_owns_transaction_data_after_the_network_buffer_is_released()
    {
        await using PeerSyncFixture fixture = PeerSyncFixture.Create();
        byte[] bytes = [1, 2, 3, 4];
        using PoisonedBodyMemory memory = new(bytes);
        Transaction transaction = Build.A.Transaction.WithData(bytes).TestObject;
        Block expected = Build.A.Block.WithTransactions(transaction).TestObject;
        fixture.Peer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => new OwnedBlockBodies([expected.Body], memory));

        Block? block = await fixture.ReadBody(expected.Header);

        Assert.That(block, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(memory.Disposed, Is.True);
            Assert.That(block.Transactions[0].Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        }
    }

    private sealed class PoisonedBodyMemory(byte[] bytes) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => bytes;
        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            bytes.AsSpan().Fill(0xff);
        }
    }

    private sealed class PeerSyncFixture : IAsyncDisposable
    {
        private readonly IContainer _container;
        private readonly Dictionary<Hash256, Block> _remote = [];
        internal IBlockTree Tree { get; }
        internal ChainEngine Engine { get; }
        internal FollowerHeads Heads { get; }
        internal ISyncPeer Peer { get; } = Substitute.For<ISyncPeer>();

        private PeerSyncFixture(IContainer container)
        {
            _container = container;
            Tree = container.Resolve<IBlockTree>();
            Engine = container.Resolve<ChainEngine>();
            Block genesis = Build.A.Block.Genesis.TestObject;
            Tree.SuggestBlock(genesis);
            Tree.TryUpdateMainChain(genesis.Header, true, true);
            Heads = new FollowerHeads(Engine, Tree, genesis.Header) { UnsafeHeadHeld = false };
            Peer.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303));
            Peer.Id.Returns(TestItem.PublicKeyB);
            Peer.HeadHash = genesis.Hash!;
            Peer.ProtocolCode.Returns("eth");
            Peer.ProtocolVersion.Returns((byte)68);
            Peer.GetHeadBlockHeader(Arg.Any<Hash256>(), Arg.Any<CancellationToken>()).Returns(call =>
                _remote.GetValueOrDefault(call.ArgAt<Hash256>(0))?.Header);
            Peer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>()).Returns(call =>
                new OwnedBlockBodies([_remote.GetValueOrDefault(call.ArgAt<IReadOnlyList<Hash256>>(0)[0])?.Body]));
            ISyncPeerPool pool = container.Resolve<ISyncPeerPool>();
            pool.Start();
            pool.AddPeer(Peer);
            Assert.That(Peer.IsInitialized, Is.True);
        }

        internal static PeerSyncFixture Create()
        {
            IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(London.Instance))
                .AddSingleton<ChainEngine>()
                .AddSingleton<IEezL2Engine, ChainEngine>(static engine => engine)
                .AddSingleton<ISpecProvider>(DerivedChain.SpecProvider)
                .AddSingleton(DerivedChain.Context)
                .AddSingleton<IDerivedHeaderRules, Rollup0HeaderRules>()
                .AddSingleton<SequencerHeadTracker>()
                .AddSingleton<SequencerPeerBlocks>()
                .AddSingleton<SequencerPeerHeadSource>()
                .Build();
            try { return new PeerSyncFixture(container); }
            catch { container.Dispose(); throw; }
        }

        internal Block[] Branch(BlockHeader parent, int count, byte marker)
        {
            Block[] blocks = new Block[count];
            for (int i = 0; i < count; i++)
            {
                blocks[i] = DerivedChain.Child(parent, TestItem.AddressA, [marker, (byte)i]);
                parent = blocks[i].Header;
            }
            return blocks;
        }

        internal void Announce(Block[] branch, ulong epoch, ulong counter = 1, bool serve = true)
        {
            if (serve)
                foreach (Block block in branch) _remote[block.Hash!] = block;
            Block head = branch[^1];
            SequencerHeadClaim claim = new(1, head.Number, head.Hash!, head.ParentHash!, epoch, counter);
            Assert.That(_container.Resolve<SequencerHeadTracker>().Observe(new VerifiedSequencerHead(claim, Signer, head), epoch),
                Is.EqualTo(SequencerHeadVerdict.Accept));
        }

        internal Task<Block?> ReadBody(BlockHeader header) => _container.Resolve<SequencerPeerBlocks>().Block(header, CancellationToken.None);

        internal Task Advance() => _container.Resolve<SequencerPeerHeadSource>().Advance(Heads, CancellationToken.None);
        public ValueTask DisposeAsync() => _container.DisposeAsync();
    }
}
