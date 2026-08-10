// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Init.FlatHistory;
using Nethermind.State;
using Nethermind.State.Flat.History;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Init.Test.FlatHistory;

public class NHistWindowImportSourceTests
{
    private static PeerInfo CreatePeer()
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        return new PeerInfo(syncPeer);
    }

    [Test]
    public async Task GetChangesetsAsync_WhenSourceHasNothing_YieldsNoChunks()
    {
        INHistSyncPeer syncPeer = Substitute.For<INHistSyncPeer>();
        syncPeer.GetChangesets(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(new NHistChangesetsPage([]));

        NHistWindowImportSource source = new(CreatePeer(), syncPeer);

        List<WindowImportChunk> chunks = await source.GetChangesetsAsync(1, 10, CancellationToken.None).ToListAsync();

        Assert.That(chunks, Is.Empty);
    }

    [Test]
    public async Task GetChangesetsAsync_OneBlockPerResponse_AdvancesFromBlockAndYieldsAllChunksInOrder()
    {
        ChangesetChunkEntry block1 = new(1, 0, true, new byte[] { 1 });
        ChangesetChunkEntry block2 = new(2, 0, true, new byte[] { 2 });
        ChangesetChunkEntry block3 = new(3, 0, true, new byte[] { 3 });

        INHistSyncPeer syncPeer = Substitute.For<INHistSyncPeer>();
        syncPeer.GetChangesets(1, 3, Arg.Any<CancellationToken>()).Returns(new NHistChangesetsPage([block1]));
        syncPeer.GetChangesets(2, 3, Arg.Any<CancellationToken>()).Returns(new NHistChangesetsPage([block2]));
        syncPeer.GetChangesets(3, 3, Arg.Any<CancellationToken>()).Returns(new NHistChangesetsPage([block3]));

        NHistWindowImportSource source = new(CreatePeer(), syncPeer);

        List<WindowImportChunk> chunks = await source.GetChangesetsAsync(1, 3, CancellationToken.None).ToListAsync();

        Assert.That(chunks.Select(c => c.Block), Is.EqualTo(new ulong[] { 1, 2, 3 }),
            "each response covering exactly one complete block must advance the next request's FromBlock past it");
    }

    [Test]
    public async Task GetChangesetsAsync_ResponseSplitsMidBlockThenCompletesOnRetry_YieldsChunksOnceInOrder()
    {
        ChangesetChunkEntry chunk0 = new(5, 0, false, new byte[] { 0xA });
        ChangesetChunkEntry chunk0Again = new(5, 0, false, new byte[] { 0xA });
        ChangesetChunkEntry chunk1 = new(5, 1, true, new byte[] { 0xB });

        INHistSyncPeer syncPeer = Substitute.For<INHistSyncPeer>();
        syncPeer.GetChangesets(5, 5, Arg.Any<CancellationToken>())
            .Returns(new NHistChangesetsPage([chunk0]), new NHistChangesetsPage([chunk0Again, chunk1]));

        NHistWindowImportSource source = new(CreatePeer(), syncPeer);

        List<WindowImportChunk> chunks = await source.GetChangesetsAsync(5, 5, CancellationToken.None).ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks.Count, Is.EqualTo(2), "a mid-block cutoff must not leak a partial, never-completed chunk sequence downstream");
            Assert.That(chunks[0].ChunkIndex, Is.EqualTo(0u));
            Assert.That(chunks[1].ChunkIndex, Is.EqualTo(1u));
            Assert.That(chunks[1].IsLastChunkForBlock, Is.True);
        }
    }

    [Test]
    public void GetChangesetsAsync_WhenSameBlockNeverCompletesTwiceInARow_ThrowsInsteadOfLoopingForever()
    {
        ChangesetChunkEntry chunk0 = new(7, 0, false, new byte[] { 0xA });

        INHistSyncPeer syncPeer = Substitute.For<INHistSyncPeer>();
        syncPeer.GetChangesets(7, 7, Arg.Any<CancellationToken>()).Returns(new NHistChangesetsPage([chunk0]));

        NHistWindowImportSource source = new(CreatePeer(), syncPeer);

        Assert.That(async () => await source.GetChangesetsAsync(7, 7, CancellationToken.None).ToListAsync(),
            Throws.TypeOf<NHistChangesetOversizedBlockException>(),
            "a block whose changeset alone always exceeds the peer's max response size can never be resumed with a range-only, no-chunk-cursor request - this must fail closed, not spin forever");
    }
}

internal static class AsyncEnumerableTestExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        List<T> result = [];
        await foreach (T item in source) result.Add(item);
        return result;
    }
}
