// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

public class RangeSyncTests
{
    private const ulong AnchorSlot = 10;
    private const ulong TargetSlot = 18;

    public enum BadPeerBehavior
    {
        WrongParentBatch,
        ThrowsMidBatch,
    }

    [TestCase(BadPeerBehavior.WrongParentBatch)]
    [TestCase(BadPeerBehavior.ThrowsMidBatch)]
    [CancelAfter(30_000)]
    public async Task Yields_continuity_verified_blocks_and_refetches_bad_batches_from_another_peer(BadPeerBehavior behavior, CancellationToken token)
    {
        // Slot 15 stays empty to exercise count-based requests returning only existing blocks.
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) =
            TestChain.BuildLinkedChain(AnchorSlot, 11, 12, 13, 14, 16, 17, 18);

        StubPeer badPeer = new("bad", headSlot: TargetSlot + 1, (startSlot, count) => behavior switch
        {
            BadPeerBehavior.ThrowsMidBatch => throw new TimeoutException("peer disconnected"),
            _ => [TestChain.CreateBlock(startSlot, parentRoot: Hash256.Zero), .. ServeRange(chain, startSlot + 1, count - 1)],
        });
        StubPeer goodPeer = new("good", headSlot: TargetSlot, (startSlot, count) => ServeRange(chain, startSlot, count));
        RangeSync sync = new(new StubPool(badPeer, goodPeer), LimboLogs.Instance);

        List<SignedBeaconBlock> imported = [];
        await foreach (SignedBeaconBlock block in sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, token))
        {
            imported.Add(block);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.Select(b => b.Message!.Slot), Is.EqualTo(chain.Select(b => b.Message!.Slot)), "import order");
            Assert.That(imported.Select(b => SszRoots.HashTreeRoot(b.Message!)), Is.EqualTo(chain.Select(b => SszRoots.HashTreeRoot(b.Message!))), "block roots");
            Assert.That(badPeer.Failures, Is.GreaterThanOrEqualTo(1), "bad peer penalized");
            Assert.That(goodPeer.Requests, Is.GreaterThanOrEqualTo(1), "good peer served the refetch");
        }
    }

    private static SignedBeaconBlock[] ServeRange(SignedBeaconBlock[] chain, ulong startSlot, ulong count) =>
        [.. chain.Where(b => b.Message!.Slot >= startSlot && b.Message.Slot < startSlot + count)];

    private sealed class StubPeer(string id, ulong headSlot, Func<ulong, ulong, SignedBeaconBlock[]> handler) : IBeaconSyncPeer
    {
        public int Failures { get; private set; }
        public int Requests { get; private set; }

        public string Id => id;
        public ulong HeadSlot => headSlot;

        public Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token)
        {
            Requests++;
            return Task.FromResult<IReadOnlyList<SignedBeaconBlock>>(handler(startSlot, count));
        }

        public Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SignedBeaconBlock>>([]);

        public void ReportFailure(string reason) => Failures++;
    }

    private sealed class StubPool(params IBeaconSyncPeer[] peers) : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) =>
            [.. peers.Where(p => p.HeadSlot >= minHeadSlot)];
    }
}
