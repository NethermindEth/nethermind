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
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Api;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

public partial class RangeSyncTests
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
            _ => [new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(startSlot, parentRoot: Hash256.Zero)), .. ServeRange(chain, startSlot + 1, count - 1)],
        });
        StubPeer goodPeer = new("good", headSlot: TargetSlot, (startSlot, count) => ServeRange(chain, startSlot, count));
        RangeSync sync = new(new StubPool(badPeer, goodPeer), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> imported = await CollectAsync(sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, token));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.Select(b => b.Slot), Is.EqualTo(chain.Select(b => b.Message!.Slot)), "import order");
            Assert.That(imported.Select(b => b.ComputeMessageRoot()), Is.EqualTo(chain.Select(b => SszRoots.HashTreeRoot(b.Message!))), "block roots");
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
            (startSlot, count) => [new ForkedSignedBeaconBlock.OfFulu(chain.Block)],
            (startSlot, count, columns) => [.. columns.Select(c => chain.Columns[(int)c])]);
        DataColumnSidecarPool sidecarPool = new();
        RangeSync sync = new(new StubPool(peer), LimboLogs.Instance, sidecarPool, chain.Spec, chain.ClockAtEpoch(0), discovery);

        List<ForkedSignedBeaconBlock> imported = await CollectAsync(sync.Run(chain.AnchorRoot, chain.AnchorBlock.Message!.Slot, () => chain.Block.Message!.Slot, token));

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

    /// <summary>
    /// fulu/fork-choice.md <c>is_data_available</c> demands no columns for a block before the data availability window, so
    /// asking a peer for them spends its rate limit on sidecars it need not serve and may penalize it for not having them.
    /// The fixture block sits in epoch 0, which leaves the window once the clock is more than the window width past it.
    /// </summary>
    [TestCase(Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests, 1)]
    [TestCase(Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 1, 0)]
    [CancelAfter(30_000)]
    public async Task Fulu_blob_blocks_before_the_data_availability_window_get_no_column_request(ulong currentEpoch, int expectedRequests, CancellationToken token)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, chain.Spec, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        StubPeer peer = new(
            "peer",
            headSlot: chain.Block.Message!.Slot,
            (startSlot, count) => [new ForkedSignedBeaconBlock.OfFulu(chain.Block)],
            (startSlot, count, columns) => [.. columns.Select(c => chain.Columns[(int)c])]);
        RangeSync sync = new(new StubPool(peer), LimboLogs.Instance, new DataColumnSidecarPool(), chain.Spec, chain.ClockAtEpoch(currentEpoch), discovery);

        List<ForkedSignedBeaconBlock> imported = await CollectAsync(sync.Run(chain.AnchorRoot, chain.AnchorBlock.Message!.Slot, () => chain.Block.Message!.Slot, token));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported, Has.Count.EqualTo(1), "the block is yielded whether or not its columns are fetched");
            Assert.That(peer.ColumnRequests, Is.EqualTo(expectedRequests));
        }
    }

    /// <summary>
    /// Gossip fills the pool with head-slot columns while range sync is still behind; the importer checks the columns range
    /// sync fetched in the same pool, so refusing them for being older than every held slot would stall sync for good.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_range_synced_blob_block_imports_while_the_pool_is_full_of_higher_slot_columns(CancellationToken token)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, chain.Spec, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        int sampled = new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns.Count;
        DataColumnSidecarPool sidecarPool = new(sampled);
        const ulong headSlot = 100;
        Hash256 headRoot = Keccak.Compute("head");
        for (int column = 0; column < sampled; column++)
        {
            sidecarPool.Add(headRoot, headSlot, chain.Columns[column]);
        }

        StubPeer peer = new(
            "peer",
            headSlot: chain.Block.Message!.Slot,
            (startSlot, count) => [new ForkedSignedBeaconBlock.OfFulu(chain.Block)],
            (startSlot, count, columns) => [.. columns.Select(c => chain.Columns[(int)c])]);
        SlotClock clock = chain.ClockAtEpoch(1);
        RangeSync sync = new(new StubPool(peer), LimboLogs.Instance, sidecarPool, chain.Spec, clock, discovery);
        IBlockImporter importer = new BlockImporterFactory(chain.Spec, store, chain.Pubkeys, new NoOpEngineDriver(), new BeaconChainConfig(), LimboLogs.Instance, sidecarPool, clock, discovery)
            .Create(new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);

        List<BlockImportResult> results = [];
        await foreach (ForkedSignedBeaconBlock block in sync.Run(chain.AnchorRoot, chain.AnchorBlock.Message!.Slot, () => chain.Block.Message!.Slot, token))
        {
            results.Add(importer.Import(block, chain.BlockRoot, verifySignatures: true));
        }

        Assert.That(results, Is.EqualTo(new[] { BlockImportResult.Imported }));
    }

    /// <summary>A wall clock stopped at genesis, which puts every Fulu or later block inside the data availability window.</summary>
    internal static SlotClock ClockAtGenesis(BeaconChainSpec spec) =>
        new(spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)spec.GenesisTime).UtcDateTime));

    private static ForkedSignedBeaconBlock[] ServeRange(SignedBeaconBlock[] chain, ulong startSlot, ulong count) =>
        [.. chain.Where(b => b.Message!.Slot >= startSlot && b.Message.Slot < startSlot + count).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];

    internal static async Task<List<ForkedSignedBeaconBlock>> CollectAsync(IAsyncEnumerable<ForkedSignedBeaconBlock> blocks)
    {
        List<ForkedSignedBeaconBlock> yielded = [];
        await foreach (ForkedSignedBeaconBlock block in blocks)
        {
            yielded.Add(block);
        }

        return yielded;
    }

    internal static async Task DrainAsync(IAsyncEnumerable<ForkedSignedBeaconBlock> blocks)
    {
        await foreach (ForkedSignedBeaconBlock _ in blocks)
        {
        }
    }

    internal sealed class StubPeer(
        string id,
        ulong headSlot,
        Func<ulong, ulong, ForkedSignedBeaconBlock[]> handler,
        Func<ulong, ulong, ulong[], DataColumnSidecar[]>? columnHandler = null,
        Func<ulong, ulong, ulong[], DataColumnSidecarGloas[]>? gloasColumnHandler = null,
        Func<DataColumnsByRootIdentifier[], DataColumnSidecarGloas[]>? gloasRootHandler = null,
        PeerColumnCustody? custody = null,
        Func<DataColumnsByRootIdentifier[], DataColumnSidecar[]>? rootHandler = null,
        ulong earliestAvailableSlot = 0,
        Func<Hash256[], ForkedSignedBeaconBlock[]>? blockRootHandler = null) : IBeaconSyncPeer
    {
        /// <summary>Every column, as a supernode would custody, unless the test narrows it.</summary>
        public PeerColumnCustody Custody { get; } = custody ?? AllColumns;

        internal static PeerColumnCustody AllColumns { get; } = new(Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c), isAdvertised: true);

        /// <summary>The columns of every by-range and by-root column request, in order.</summary>
        public List<ulong[]> RequestedColumns { get; } = [];
        public List<ulong[]> RequestedGloasColumns { get; } = [];
        public int RootColumnRequests { get; private set; }
        public int RootBlockRequests { get; private set; }
        public List<(ulong Start, ulong Count)> RequestedRanges { get; } = [];

        public List<PeerFailureReason> Reports { get; } = [];
        public int Failures => Reports.Count;
        public int Requests { get; private set; }
        public int ColumnRequests { get; private set; }

        public string Id => id;
        public ulong HeadSlot => headSlot;
        public ulong EarliestAvailableSlot => earliestAvailableSlot;

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token)
        {
            Requests++;
            RequestedRanges.Add((startSlot, count));
            return Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>(handler(startSlot, count));
        }

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token)
        {
            RootBlockRequests++;
            return Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>(blockRootHandler?.Invoke(roots) ?? []);
        }

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token)
        {
            ColumnRequests++;
            RequestedColumns.Add(columns);
            return Task.FromResult<IReadOnlyList<DataColumnSidecar>>(columnHandler?.Invoke(startSlot, count, columns) ?? []);
        }

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token)
        {
            RootColumnRequests++;
            foreach (DataColumnsByRootIdentifier identifier in identifiers)
            {
                RequestedColumns.Add(identifier.Columns!);
            }

            return Task.FromResult<IReadOnlyList<DataColumnSidecar>>(rootHandler?.Invoke(identifiers) ?? []);
        }

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token)
        {
            RequestedGloasColumns.Add(columns);
            return Task.FromResult<IReadOnlyList<DataColumnSidecarGloas>>(gloasColumnHandler is null ? throw new NotSupportedException() : gloasColumnHandler(startSlot, count, columns));
        }

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DataColumnSidecarGloas>>(gloasRootHandler is null ? throw new NotSupportedException() : gloasRootHandler(identifiers));

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong startSlot, ulong count, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] roots, CancellationToken token) =>
            throw new NotSupportedException();

        public void ReportFailure(PeerFailureReason reason, string? detail = null) => Reports.Add(reason);
    }

    internal sealed class FixedIPResolver(IPAddress ip) : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default) =>
            new(new IIPResolver.NethermindIp(ip, ip));
    }

    internal sealed class StubPool(params IBeaconSyncPeer[] peers) : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) =>
            [.. peers.Where(p => p.HeadSlot >= minHeadSlot)];
    }
}
