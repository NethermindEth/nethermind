// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Proofs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;
using IContainer = Autofac.IContainer;

namespace Nethermind.Synchronization.Test;

// EIP-8141 and EIP-8512: neither headers nor bodies carry the dependency proof, so forward sync asks for it in
// requests of its own, after the body, through a peer the pool allocates.
public partial class BlockDownloaderTests
{
    private static readonly byte[][] DependencyProofs = [[1, 1], [2, 2]];
    private static readonly Transaction BodyTransaction = Build.A.Transaction.SignedAndResolved().TestObject;

    [Test]
    public async Task Dependency_proofs_are_requested_after_bodies_in_requests_of_their_own()
    {
        (IContainer node, Context ctx, BlockHeader[] headers) = await DependencyProofNode();
        await using IContainer scope = node;

        BlocksRequest bodies = await Prepare(ctx);
        Hash256?[] bodyHashes = [.. bodies.BodiesRequests.Select(static h => h.Hash)];
        int proofsWithBodies = bodies.DependencyProofsRequests.Count;
        using BlocksRequest proofs = await ServeBodiesAndPrepare(ctx, bodies);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bodyHashes, Is.EqualTo(headers.Select(static h => h.Hash)));
            Assert.That(proofsWithBodies, Is.Zero, "no proof is requested before its block has a body");
            Assert.That(proofs.BodiesRequests, Is.Empty);
            Assert.That(proofs.DependencyProofsRequests.Select(static h => h.Hash), Is.EqualTo(headers.Select(static h => h.Hash)));
        }
    }

    // Proofs may be pruned (EIP-8512), so blocks inserted without processing neither request nor wait for them.
    [Test]
    public async Task Inserted_blocks_neither_request_nor_wait_for_dependency_proofs()
    {
        (IContainer node, Context ctx, BlockHeader[] headers) = await DependencyProofNode();
        await using IContainer scope = node;

        BlocksRequest bodies = await Prepare(ctx, DownloaderOptions.Insert);
        using BlocksRequest? afterBodies = await ServeBodiesAndPrepareOrNull(ctx, bodies, DownloaderOptions.Insert);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterBodies?.DependencyProofsRequests, Is.Null.Or.Empty);
            Assert.That(ctx.BlockTree.BestSuggestedHeader?.Hash, Is.EqualTo(headers[^1].Hash));
        }
    }

    // A proof response that times out, or one still in flight, neither requests the bodies again nor duplicates the proofs.
    [Test]
    public async Task Slow_dependency_proofs_do_not_request_bodies_again()
    {
        (IContainer node, Context ctx, _) = await DependencyProofNode();
        await using IContainer scope = node;
        BlocksRequest bodies = await Prepare(ctx);
        BlocksRequest proofs = await ServeBodiesAndPrepare(ctx, bodies);

        using BlocksRequest? whileInFlight = await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
        proofs.DownloadTask = Task.FromException(new TimeoutException());
        ctx.FullSyncFeedComponent.BlockDownloader.HandleResponse(proofs, Peer(EthVersions.Eth74));
        using BlocksRequest afterTimeout = await Prepare(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileInFlight, Is.Null, "nothing is requested again while the proof request is outstanding");
            Assert.That(afterTimeout.BodiesRequests, Is.Empty);
            Assert.That(afterTimeout.DependencyProofsRequests, Has.Count.EqualTo(DependencyProofs.Length));
        }
    }

    // EIP-8512: an empty entry, or the tail of a response cut short by the soft limit, is requested again without penalty;
    // a non-empty proof that does not match its header is a breach of protocol.
    [TestCase(new[] { "match", "empty" }, false, 1)]
    [TestCase(new[] { "match" }, false, 1)]
    [TestCase(new[] { "mismatch", "match" }, true, 0)]
    public async Task Dependency_proof_responses_follow_eip8512(string[] entries, bool breach, int requestedAgain)
    {
        (IContainer node, Context ctx, BlockHeader[] headers) = await DependencyProofNode();
        await using IContainer scope = node;
        BlocksRequest bodies = await Prepare(ctx);
        BlocksRequest proofs = await ServeBodiesAndPrepare(ctx, bodies);
        PeerInfo peer = Peer(EthVersions.Eth74);
        proofs.DependencyProofs = new ArrayPoolList<byte[]>(entries.Length, entries.Select((entry, i) => entry switch
        {
            "match" => DependencyProofs[i],
            "mismatch" => new byte[] { 9 },
            _ => []
        }));

        ctx.FullSyncFeedComponent.BlockDownloader.HandleResponse(proofs, peer);
        using BlocksRequest next = await Prepare(ctx);

        using (Assert.EnterMultipleScope())
        {
            ctx.PeerPool.Received(breach ? 1 : 0).ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
            Assert.That(next.DependencyProofsRequests.Select(static h => h.Hash), Is.EqualTo(new[] { headers[requestedAgain].Hash }));
        }
    }

    // A synced block committing to the empty proof stores it, so payload bodies and eth/74 serve it as for a newPayload block.
    [Test]
    public async Task Synced_block_without_dependencies_stores_the_empty_proof()
    {
        (IContainer node, Context ctx, BlockHeader[] headers) = await DependencyProofNode(emptyProofs: true);
        await using IContainer scope = node;
        BlocksRequest bodies = await Prepare(ctx);

        using BlocksRequest? next = await ServeBodiesAndPrepareOrNull(ctx, bodies);

        IDependencyProofStore proofStore = node.Resolve<IDependencyProofStore>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(next?.DependencyProofsRequests.Count ?? 0, Is.Zero, "the empty proof is not requested");
            foreach (BlockHeader header in headers) Assert.That(proofStore.Get(header.Number, header.Hash!), Is.Empty);
        }
    }

    [Test]
    public void Dependency_proof_requests_are_allocated_to_eth74_peers_at_or_above_the_blocks()
    {
        using BlocksRequest request = new() { DependencyProofsRequests = new ArrayPoolList<BlockHeader>(1) { Build.A.BlockHeader.WithNumber(10).TestObject } };
        PeerInfo eth72 = Peer(EthVersions.Eth72, 20);
        PeerInfo behind = Peer(EthVersions.Eth74, 9);
        PeerInfo eth74 = Peer(EthVersions.Eth74, 10);

        IPeerAllocationStrategy strategy = new BlocksSyncPeerAllocationStrategyFactory().Create(request);
        PeerInfo? allocated = strategy.Allocate(null, [eth72, behind, eth74], Substitute.For<INodeStatsManager>(), Substitute.For<IBlockTree>());

        Assert.That(allocated, Is.SameAs(eth74));
    }

    private Task<(IContainer Node, Context Ctx, BlockHeader[] Headers)> DependencyProofNode(bool emptyProofs = false)
    {
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        IContainer node = CreateNode(builder => builder
            .AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider)
            .AddSingleton<IBlockValidator>(Always.Valid));
        Context ctx = node.Resolve<Context>();
        BlockHeader[] chain = new BlockHeader[DependencyProofs.Length + 1];
        chain[0] = ctx.BlockTree.Genesis!;
        for (int i = 0; i < DependencyProofs.Length; i++)
        {
            BlockHeader header = Build.A.BlockHeader.WithParent(chain[i]).WithTransactionsRoot(TxTrie.CalculateRoot([BodyTransaction])).TestObject;
            header.DependencyProofHash = emptyProofs ? Keccak.OfAnEmptyString : Keccak.Compute(DependencyProofs[i]);
            header.Hash = header.CalculateHash();
            chain[i + 1] = header;
        }

        forwardHeaderProvider.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // From the best suggested block on, as the real provider serves them.
                BlockHeader[] remaining = chain[(int)(ctx.BlockTree.BestSuggestedHeader?.Number ?? 0)..];
                return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(new ArrayPoolList<BlockHeader?>(remaining.Length, remaining));
            });
        ctx.PeerPool.EstimateRequestLimit(Arg.Any<RequestType>(), Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<int?>(DependencyProofs.Length));
        return Task.FromResult((node, ctx, chain[1..]));
    }

    private static async Task<BlocksRequest> Prepare(Context ctx, DownloaderOptions options = DownloaderOptions.Process) =>
        (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(options, 0, CancellationToken.None))
        ?? throw new AssertionException("No request was prepared.");

    private static async Task<BlocksRequest> ServeBodiesAndPrepare(Context ctx, BlocksRequest bodies) =>
        await ServeBodiesAndPrepareOrNull(ctx, bodies) ?? throw new AssertionException("No request was prepared.");

    private static async Task<BlocksRequest?> ServeBodiesAndPrepareOrNull(Context ctx, BlocksRequest bodies,
        DownloaderOptions options = DownloaderOptions.Process)
    {
        bodies.OwnedBodies = new OwnedBlockBodies([.. bodies.BodiesRequests.Select(static _ => new BlockBody([BodyTransaction], []))]);
        ctx.FullSyncFeedComponent.BlockDownloader.HandleResponse(bodies, Peer(EthVersions.Eth74));
        return await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(options, 0, CancellationToken.None);
    }

    private static PeerInfo Peer(byte version, ulong head = 100)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(version);
        syncPeer.HeadNumber.Returns(head);
        syncPeer.IsInitialized.Returns(true);
        return new PeerInfo(syncPeer);
    }
}
