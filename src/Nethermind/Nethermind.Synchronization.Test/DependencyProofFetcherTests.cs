// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

[Parallelizable(ParallelScope.All)]
public class DependencyProofFetcherTests
{
    private static readonly byte[] Proof = [1, 2, 3];

    [Test]
    public async Task Accepts_a_proof_that_matches_its_header()
    {
        BlockHeader header = Committed(10, Proof);
        PeerInfo source = Peer(10, [Proof]);
        (DependencyProofFetcher fetcher, ISyncPeerPool pool) = Fetcher([]);

        (byte[]?[] proofs, bool[] unavailable) = await fetcher.FetchAsync(source, [header], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new[] { Proof }));
            Assert.That(unavailable, Is.EqualTo(new[] { false }));
            pool.DidNotReceiveWithAnyArgs().ReportBreachOfProtocol(default!, default, default!);
        }
    }

    // EIP-8498: an empty entry for a block with dependencies is an unavailable proof, not an invalid response, so another
    // peer is asked and the first is not penalised.
    [Test]
    public async Task Retries_an_unavailable_proof_on_another_peer_without_penalty()
    {
        BlockHeader header = Committed(10, Proof);
        PeerInfo source = Peer(10, [[]]);
        PeerInfo other = Peer(10, [Proof]);
        (DependencyProofFetcher fetcher, ISyncPeerPool pool) = Fetcher([source, other]);

        (byte[]?[] proofs, bool[] unavailable) = await fetcher.FetchAsync(source, [header], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new[] { Proof }));
            Assert.That(unavailable, Is.EqualTo(new[] { false }));
            pool.DidNotReceiveWithAnyArgs().ReportBreachOfProtocol(default!, default, default!);
        }
    }

    [Test]
    public async Task Marks_a_proof_no_peer_serves_unavailable()
    {
        BlockHeader header = Committed(10, Proof);
        PeerInfo source = Peer(10, [[]]);
        (DependencyProofFetcher fetcher, ISyncPeerPool pool) = Fetcher([]);

        (byte[]?[] proofs, bool[] unavailable) = await fetcher.FetchAsync(source, [header], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new byte[]?[] { null }));
            Assert.That(unavailable, Is.EqualTo(new[] { true }));
            pool.DidNotReceiveWithAnyArgs().ReportBreachOfProtocol(default!, default, default!);
        }
    }

    // A batch larger than the request cap is split, and a response cut short by the soft limit is continued on the same peer.
    [Test]
    public async Task Splits_requests_and_continues_a_truncated_response_on_the_same_peer()
    {
        const int count = DependencyProofFetcher.MaxProofsPerRequest + 10;
        BlockHeader[] headers = new BlockHeader[count];
        Dictionary<Hash256, byte[]> served = [];
        for (int n = 0; n < count; n++)
        {
            byte[] proof = [(byte)n, 1];
            headers[n] = Committed((ulong)(n + 1), proof);
            served[headers[n].Hash!] = proof;
        }

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(EthVersions.Eth74);
        syncPeer.HeadNumber.Returns((ulong)count);
        List<int> requestSizes = [];
        syncPeer.GetDependencyProofs(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            IReadOnlyList<Hash256> hashes = call.Arg<IReadOnlyList<Hash256>>();
            requestSizes.Add(hashes.Count);
            // Serve at most 5 per response, as a peer at its soft limit would.
            ArrayPoolList<byte[]> response = new(5);
            for (int n = 0; n < hashes.Count && n < 5; n++) response.Add(served[hashes[n]]);
            return Task.FromResult<IOwnedReadOnlyList<byte[]>>(response);
        });
        (DependencyProofFetcher fetcher, ISyncPeerPool pool) = Fetcher([]);

        (byte[]?[] proofs, bool[] unavailable) = await fetcher.FetchAsync(new PeerInfo(syncPeer), headers, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unavailable, Has.None.True);
            for (int n = 0; n < count; n++) Assert.That(proofs[n], Is.EqualTo(served[headers[n].Hash!]));
            Assert.That(requestSizes, Has.All.LessThanOrEqualTo(DependencyProofFetcher.MaxProofsPerRequest));
            pool.DidNotReceiveWithAnyArgs().ReportBreachOfProtocol(default!, default, default!);
        }
    }

    [Test]
    public async Task Rejects_a_proof_that_does_not_match_its_header()
    {
        BlockHeader header = Committed(10, Proof);
        PeerInfo source = Peer(10, [[9]]);
        (DependencyProofFetcher fetcher, ISyncPeerPool pool) = Fetcher([]);

        (byte[]?[] proofs, bool[] unavailable) = await fetcher.FetchAsync(source, [header], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new byte[]?[] { null }));
            Assert.That(unavailable, Is.EqualTo(new[] { true }), "the block waits for a matching proof");
            pool.Received(1).ReportBreachOfProtocol(source, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
        }
    }

    // A block without dependencies commits to the empty proof, a pre-fork block to none, and a finalized block is trusted.
    [Test]
    public async Task Requests_only_unfinalized_blocks_with_dependencies()
    {
        BlockHeader noDependencies = Committed(11, []);
        BlockHeader preFork = Build.A.BlockHeader.WithNumber(12).TestObject;
        BlockHeader finalized = Committed(5, Proof);
        PeerInfo source = Peer(12, []);
        (DependencyProofFetcher fetcher, _) = Fetcher([], finalized);

        (byte[]?[] proofs, bool[] unavailable) = await fetcher.FetchAsync(source, [finalized, noDependencies, preFork], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new byte[]?[] { null, [], null }));
            Assert.That(unavailable, Is.EqualTo(new[] { false, false, false }));
            await source.SyncPeer.DidNotReceiveWithAnyArgs().GetDependencyProofs(default!, default);
        }
    }

    // A side-chain block below the finalized height is not covered by finality, so its proof is fetched.
    [Test]
    public async Task Requests_a_side_chain_block_below_finality()
    {
        BlockHeader sibling = Committed(5, Proof);
        PeerInfo source = Peer(12, [Proof]);
        (DependencyProofFetcher fetcher, _) = Fetcher([], Committed(8, [9]));

        (byte[]?[] proofs, bool[] unavailable) = await fetcher.FetchAsync(source, [sibling], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new[] { Proof }));
            Assert.That(unavailable, Is.EqualTo(new[] { false }));
        }
    }

    [Test]
    public async Task Skips_a_canonical_ancestor_of_the_finalized_block()
    {
        BlockHeader ancestor = Committed(5, Proof);
        PeerInfo source = Peer(12, [Proof]);
        (DependencyProofFetcher fetcher, IBlockTree blockTree) = FetcherWithTree(Committed(8, [9]));
        blockTree.FindLevel(5).Returns(new ChainLevelInfo(true, new BlockInfo(ancestor.Hash!, 0)));

        (byte[]?[] proofs, bool[] unavailable) = await fetcher.FetchAsync(source, [ancestor], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new byte[]?[] { null }));
            Assert.That(unavailable, Is.EqualTo(new[] { false }));
            await source.SyncPeer.DidNotReceiveWithAnyArgs().GetDependencyProofs(default!, default);
        }
    }

    private static (DependencyProofFetcher Fetcher, IBlockTree BlockTree) FetcherWithTree(BlockHeader finalized)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindFinalizedHeader().Returns(finalized);
        return (new DependencyProofFetcher(Substitute.For<ISyncPeerPool>(), blockTree, LimboLogs.Instance), blockTree);
    }

    private static (DependencyProofFetcher Fetcher, ISyncPeerPool Pool) Fetcher(IEnumerable<PeerInfo> peers, BlockHeader? finalized = null)
    {
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns(peers);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindFinalizedHeader().Returns(finalized);
        return (new DependencyProofFetcher(pool, blockTree, LimboLogs.Instance), pool);
    }

    private static PeerInfo Peer(ulong head, byte[][] response)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(EthVersions.Eth74);
        syncPeer.HeadNumber.Returns(head);
        syncPeer.GetDependencyProofs(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IOwnedReadOnlyList<byte[]>>(new ArrayPoolList<byte[]>(response.Length, response)));
        return new PeerInfo(syncPeer);
    }

    private static BlockHeader Committed(ulong number, byte[] proof)
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).TestObject;
        header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(proof), Keccak.Compute("deps"));
        header.Hash = header.CalculateHash();
        return header;
    }
}
