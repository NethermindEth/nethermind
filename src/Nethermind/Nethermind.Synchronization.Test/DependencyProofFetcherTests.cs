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

        byte[]?[] proofs = await fetcher.FetchAsync(source, [header], CancellationToken.None);

        Assert.That(proofs, Is.EqualTo(new[] { Proof }));
        pool.DidNotReceiveWithAnyArgs().ReportBreachOfProtocol(default!, default, default!);
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

        byte[]?[] proofs = await fetcher.FetchAsync(source, [header], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new[] { Proof }));
            pool.DidNotReceiveWithAnyArgs().ReportBreachOfProtocol(default!, default, default!);
        }
    }

    [Test]
    public async Task Rejects_a_proof_that_does_not_match_its_header()
    {
        BlockHeader header = Committed(10, Proof);
        PeerInfo source = Peer(10, [[9]]);
        (DependencyProofFetcher fetcher, ISyncPeerPool pool) = Fetcher([]);

        byte[]?[] proofs = await fetcher.FetchAsync(source, [header], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new byte[]?[] { null }));
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

        byte[]?[] proofs = await fetcher.FetchAsync(source, [finalized, noDependencies, preFork], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs, Is.EqualTo(new byte[]?[] { null, [], null }));
            await source.SyncPeer.DidNotReceiveWithAnyArgs().GetDependencyProofs(default!, default);
        }
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
