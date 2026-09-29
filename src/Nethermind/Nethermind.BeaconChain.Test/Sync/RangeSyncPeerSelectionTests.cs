// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.RangeSyncTests;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// fulu/p2p-interface.md DataColumnSidecarsByRange: a peer serves a range only from its Status v2 <c>earliest_available_slot</c>,
/// and it can serve the range once its head reaches the range's first slot, not its last.
/// </summary>
public class RangeSyncPeerSelectionTests
{
    private const ulong AnchorSlot = 10;
    private const ulong TargetSlot = 18;

    /// <summary>The first slot of the batch is 11, so a peer that has nothing before 12 cannot serve it, and one starting at 11 can.</summary>
    [TestCase(0UL, true)]
    [TestCase(11UL, true)]
    [TestCase(12UL, false)]
    [CancelAfter(30_000)]
    public async Task Blocks_by_range_skip_a_peer_whose_earliest_available_slot_is_above_the_batch_start(ulong earliestAvailableSlot, bool served, CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 11, 12, 13, 14, 16, 17, 18);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        // First in the pool, so the round-robin asks it first whenever it is eligible.
        StubPeer pruned = new("pruned", TargetSlot, (_, _) => chainBlocks, earliestAvailableSlot: earliestAvailableSlot);
        StubPeer full = new("full", TargetSlot, (_, _) => chainBlocks);
        RangeSync sync = new(new StubPool(pruned, full), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> yielded = [];
        await foreach (ForkedSignedBeaconBlock block in sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, token))
        {
            yielded.Add(block);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(yielded, Has.Count.EqualTo(chain.Length));
            Assert.That(pruned.Requests, Is.EqualTo(served ? 1 : 0), "the peer is asked only when its earliest available slot is at or before the batch start");
            Assert.That(full.Requests, Is.EqualTo(served ? 0 : 1));
        }
    }

    /// <summary>
    /// The batch holds a blob block at slot 1 and an empty one at slot 2, so its column request runs from slot 1 to slot 2.
    /// </summary>
    [TestCase(1UL, 0UL, true, TestName = "A head at the request start serves it although it is below the request end")]
    [TestCase(0UL, 0UL, false, TestName = "A head below the request start does not")]
    [TestCase(2UL, 1UL, true, TestName = "An earliest available slot equal to the request start is allowed")]
    [TestCase(2UL, 2UL, false, TestName = "An earliest available slot above the request start is not, although it is inside the range")]
    [CancelAfter(60_000)]
    public async Task Columns_by_range_go_to_custodians_whose_head_reaches_the_start_slot_and_whose_earliest_available_slot_covers_it(
        ulong headSlot, ulong earliestAvailableSlot, bool asked, CancellationToken token)
    {
        await using ColumnBatch batch = ColumnBatch.Create();
        StubPeer blocks = new("blocks", 2, (_, _) => batch.Blocks, custody: PeerColumnCustody.None);
        StubPeer custodian = new(
            "custodian",
            headSlot,
            (_, _) => batch.Blocks,
            (_, _, columns) => [.. columns.Select(c => batch.Chain.Columns[(int)c])],
            custody: new PeerColumnCustody(batch.Sampled, isAdvertised: true),
            earliestAvailableSlot: earliestAvailableSlot);

        await batch.RunAsync(token, blocks, custodian);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(custodian.ColumnRequests, Is.EqualTo(asked ? 1 : 0));
            Assert.That(batch.Sampled.All(c => batch.SidecarPool.TryGet(batch.Chain.BlockRoot, c, out _)), Is.EqualTo(asked), "the sampled columns arrive exactly when the custodian is asked");
        }
    }

    private sealed class ColumnBatch : IAsyncDisposable
    {
        private BeaconDiscovery _discovery = null!;

        public ImportableBlobBlock Chain { get; } = ImportableBlobBlock.Create();
        public DataColumnSidecarPool SidecarPool { get; } = new();
        public ulong[] Sampled { get; private set; } = [];

        /// <summary>The blob block at slot 1 and an empty child at slot 2.</summary>
        public ForkedSignedBeaconBlock[] Blocks { get; private set; } = [];

        public static ColumnBatch Create()
        {
            ColumnBatch batch = new();
            batch._discovery = new BeaconDiscovery(new BeaconChainConfig { Discv5Port = 0 }, batch.Chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
            batch._discovery.CreateDiscv5Services(IPAddress.Loopback);
            batch.Sampled = [.. new DiscoveryNodeCustodySource(batch._discovery).Current!.SampledColumns];
            batch.Blocks =
            [
                new ForkedSignedBeaconBlock.OfFulu(batch.Chain.Block),
                new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(2, batch.Chain.BlockRoot)),
            ];
            return batch;
        }

        public async Task RunAsync(CancellationToken token, params IBeaconSyncPeer[] peers)
        {
            RangeSync sync = new(new StubPool(peers), LimboLogs.Instance, SidecarPool, Chain.Spec, Chain.ClockAtEpoch(1), _discovery);
            await foreach (ForkedSignedBeaconBlock _ in sync.Run(Chain.AnchorRoot, Chain.AnchorBlock.Message!.Slot, () => 2, token))
            {
            }
        }

        public ValueTask DisposeAsync() => _discovery.DisposeAsync();
    }
}
