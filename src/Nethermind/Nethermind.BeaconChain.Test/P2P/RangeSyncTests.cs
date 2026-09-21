// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
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
        RangeSync sync = new(new StubPool(badPeer, goodPeer), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet);

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

    /// <summary>
    /// Base case for gap 49 ("range sync cannot satisfy the data availability gate"): before this fix
    /// nothing in <c>Sync/</c> ever called the by-range column request, so a range-synced blob block's
    /// sampled columns were never in the pool and <see cref="DataAvailability.CustodySamplingAvailability"/>
    /// rejected it forever.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Range_synced_blob_block_gets_its_sampled_columns_fetched_and_verified(CancellationToken token)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, chain.Spec, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        // Resolves the identity and local custody exactly as Start does, without binding a socket.
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        NodeColumnCustody custody = new DiscoveryNodeCustodySource(discovery).Current!;

        StubPeer peer = new(
            "peer",
            headSlot: chain.Block.Message!.Slot,
            (startSlot, count) => [chain.Block],
            (startSlot, count, columns) => [.. columns.Select(c => chain.Columns[(int)c])]);
        DataColumnSidecarPool sidecarPool = new();
        RangeSync sync = new(new StubPool(peer), LimboLogs.Instance, sidecarPool, chain.Spec, discovery);

        List<SignedBeaconBlock> imported = [];
        await foreach (SignedBeaconBlock block in sync.Run(chain.AnchorRoot, chain.AnchorBlock.Message!.Slot, () => chain.Block.Message!.Slot, token))
        {
            imported.Add(block);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported, Has.Count.EqualTo(1), "the one blob block is yielded");
            Assert.That(peer.ColumnRequests, Is.EqualTo(1), "columns are fetched once for the batch");
            foreach (ulong column in custody.SampledColumns)
            {
                bool held = sidecarPool.TryGet(chain.BlockRoot, column, out DataColumnSidecar? sidecar);
                Assert.That(held, Is.True, $"sampled column {column} must be held in the pool after range sync");
                Assert.That(sidecar!.Index, Is.EqualTo(column));
            }
        }
    }

    private static SignedBeaconBlock[] ServeRange(SignedBeaconBlock[] chain, ulong startSlot, ulong count) =>
        [.. chain.Where(b => b.Message!.Slot >= startSlot && b.Message.Slot < startSlot + count)];

    private sealed class StubPeer(
        string id,
        ulong headSlot,
        Func<ulong, ulong, SignedBeaconBlock[]> handler,
        Func<ulong, ulong, ulong[], DataColumnSidecar[]>? columnHandler = null) : IBeaconSyncPeer
    {
        public int Failures { get; private set; }
        public int Requests { get; private set; }
        public int ColumnRequests { get; private set; }

        public string Id => id;
        public ulong HeadSlot => headSlot;

        public Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token)
        {
            Requests++;
            return Task.FromResult<IReadOnlyList<SignedBeaconBlock>>(handler(startSlot, count));
        }

        public Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SignedBeaconBlock>>([]);

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token)
        {
            ColumnRequests++;
            return Task.FromResult<IReadOnlyList<DataColumnSidecar>>(columnHandler?.Invoke(startSlot, count, columns) ?? []);
        }

        public void ReportFailure(PeerFailureReason reason, string? detail = null) => Failures++;
    }

    private sealed class FixedIPResolver(IPAddress ip) : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default) =>
            new(new IIPResolver.NethermindIp(ip, ip));
    }

    private sealed class StubPool(params IBeaconSyncPeer[] peers) : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) =>
            [.. peers.Where(p => p.HeadSlot >= minHeadSlot)];
    }
}
