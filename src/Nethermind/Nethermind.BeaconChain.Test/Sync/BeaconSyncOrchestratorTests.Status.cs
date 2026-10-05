// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    private const ulong FuluAnchorSlot = 13_200_000;
    private const ulong FuluHeadOffset = 64;
    private const ulong ServeRangeStart = FuluAnchorSlot + 100 * 32;
    private const ulong FuluWallSlot = FuluAnchorSlot + (4096 + 100) * 32 + 5;
    private const ulong YoungWallSlot = FuluAnchorSlot + 100;

    [TestCase(new long[] { -5 }, 0L, TestName = "Columns held from below the serve range advertise the anchor")]
    [TestCase(new long[] { 10 }, 3200L + 10L, TestName = "Columns held only from inside the serve range advertise their first slot")]
    [TestCase(new long[0], (long)(FuluWallSlot - FuluAnchorSlot + 1), TestName = "No columns held advertise the slot after the current one")]
    public async Task Status_advertises_the_earliest_slot_with_every_column_held(long[] columnSlotOffsets, long expectedOffset)
    {
        DataColumnSidecarPool pool = new();
        foreach (long offset in columnSlotOffsets)
        {
            AddColumn(pool, (ulong)((long)ServeRangeStart + offset));
        }

        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, FuluAnchorSlot, FuluWallSlot);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo((ulong)((long)FuluAnchorSlot + expectedOffset)));
    }

    [Test]
    public async Task A_node_missing_blocks_of_the_serve_range_advertises_its_anchor()
    {
        DataColumnSidecarPool pool = new();
        AddColumn(pool, FuluAnchorSlot + 10);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, FuluAnchorSlot, YoungWallSlot);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(FuluAnchorSlot));
    }

    [TestCase(-1000L, -1000L, TestName = "Blocks and columns backfilled below the anchor advertise the slot they are held from")]
    [TestCase(40L, 0L, TestName = "A backfill that has not reached the anchor advertises the anchor")]
    public async Task Status_advertises_the_slot_the_backfill_holds_blocks_from(long backfilledOffset, long expectedOffset)
    {
        DataColumnSidecarPool pool = new();
        AddColumn(pool, FuluAnchorSlot + 10);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, FuluAnchorSlot, YoungWallSlot, backfilledFrom: (ulong)((long)FuluAnchorSlot + backfilledOffset));

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo((ulong)((long)FuluAnchorSlot + expectedOffset)));
    }

    [Test]
    public async Task Status_advertises_the_start_of_the_serve_range_once_the_backfill_reached_it()
    {
        DataColumnSidecarPool pool = new();
        AddColumn(pool, FuluAnchorSlot + 10);
        ulong serveFrom = DataAvailabilityBoundary.ComputeStartSlot(new SlotClock(Spec, new ManualTimestamper(WallTime(YoungWallSlot))).CurrentEpoch, Spec);
        pool.LowerCompletelyServableFloor(serveFrom);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, FuluAnchorSlot, YoungWallSlot, backfilledFrom: serveFrom);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(serveFrom));
    }

    [Test]
    public async Task Status_earliest_slot_rises_when_the_pool_evicts_below_it()
    {
        DataColumnSidecarPool pool = new(1);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, FuluAnchorSlot, FuluWallSlot);
        AddColumn(pool, ServeRangeStart + 1);
        await orchestrator.RunHeadStepAsync(CancellationToken.None);
        ulong before = statusHolder.CurrentStatus.EarliestAvailableSlot;

        AddColumn(pool, ServeRangeStart + 2);
        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(before, Is.EqualTo(ServeRangeStart + 1));
        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(ServeRangeStart + 2), "the first slot of the range lost its column");
    }

    [Test]
    public async Task Status_earliest_slot_follows_the_column_pool_from_fulu([Values] bool pastFulu)
    {
        ulong fuluStart = Spec.FuluForkEpoch * Spec.SlotsPerEpoch;
        ulong anchorSlot = fuluStart - 40;
        DataColumnSidecarPool pool = new();
        AddColumn(pool, fuluStart + 5);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, anchorSlot, pastFulu ? fuluStart + 10 : fuluStart - 1);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(pastFulu ? fuluStart + 5 : anchorSlot));
    }

    [Test]
    public async Task Status_earliest_slot_is_computed_when_the_status_is_read()
    {
        DataColumnSidecarPool pool = new(1);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, FuluAnchorSlot, FuluWallSlot);
        AddColumn(pool, ServeRangeStart + 1);
        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        AddColumn(pool, ServeRangeStart + 2);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(ServeRangeStart + 2), "status read");
        Assert.That(statusHolder.CurrentHead.Status.EarliestAvailableSlot, Is.EqualTo(ServeRangeStart + 2), "status and head read together");
    }

    [TestCase(3UL, 0L, true, TestName = "Blob window open just after Fulu, every block held")]
    [TestCase(4095UL, 0L, true, TestName = "Blob window open on its last epoch, every block held")]
    [TestCase(3UL, 1L, false, TestName = "Blob window open, first block of the period missing")]
    [TestCase(4096UL, -40L, false, TestName = "Blob window closed")]
    public async Task Status_earliest_slot_is_the_fork_slot_while_the_blob_window_reaches_before_fulu(ulong epochsPastFulu, long anchorOffsetFromWindowStart, bool expectFuluSlot)
    {
        ulong fuluStart = Spec.FuluForkEpoch * Spec.SlotsPerEpoch;
        ulong blobWindowStart = (Spec.FuluForkEpoch + epochsPastFulu - DataAvailabilityBoundary.MinEpochsForBlobSidecarsRequests) * Spec.SlotsPerEpoch;
        ulong anchorSlot = (ulong)((long)blobWindowStart + anchorOffsetFromWindowStart);
        DataColumnSidecarPool pool = new();
        AddColumn(pool, fuluStart);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, anchorSlot, (Spec.FuluForkEpoch + epochsPastFulu) * Spec.SlotsPerEpoch + 5);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(expectFuluSlot ? fuluStart : anchorSlot));
    }

    [Test]
    public async Task Status_earliest_slot_is_the_fork_slot_for_a_genesis_anchor_before_the_blob_window_has_a_start()
    {
        BeaconChainSpec spec = new()
        {
            SecondsPerSlot = 12,
            SlotsPerEpoch = 32,
            GenesisTime = Spec.GenesisTime,
            GenesisValidatorsRoot = Hash256.Zero,
            Forks = [new(Bytes.FromHexString("0x06000000"), 0)],
            BlobSchedule = [],
            ElectraForkEpoch = 0,
            FuluForkEpoch = 2,
            MaxBlobsPerBlockElectra = 9,
            GloasForkEpoch = ulong.MaxValue,
            GloasForkVersion = Bytes.FromHexString("0x07000000"),
            Bootnodes = [],
        };
        DataColumnSidecarPool pool = new();
        AddColumn(pool, 2 * spec.SlotsPerEpoch);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, 0, 10 * spec.SlotsPerEpoch + 5, forkSpec: spec);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(2 * spec.SlotsPerEpoch));
    }

    [TestCase(new long[0], FuluHeadOffset, TestName = "The advertised slot is served by range with no columns held")]
    [TestCase(new long[] { 10 }, 5UL, TestName = "The advertised slot is served by range with the head below the first held column")]
    public async Task Status_earliest_slot_is_served_by_range(long[] columnSlotOffsets, ulong headOffset)
    {
        DataColumnSidecarPool pool = new();
        foreach (long offset in columnSlotOffsets)
        {
            AddColumn(pool, (ulong)((long)ServeRangeStart + offset));
        }

        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, SlotClock slotClock) = CreateStatusHarness(pool, FuluAnchorSlot, FuluWallSlot, headOffset);
        ulong atStartup = statusHolder.CurrentStatus.EarliestAvailableSlot;
        await orchestrator.RunHeadStepAsync(CancellationToken.None);
        ulong afterHeadStep = statusHolder.CurrentStatus.EarliestAvailableSlot;

        Assert.DoesNotThrowAsync(() => RequestOneSlotByRangeAsync(pool, slotClock, atStartup), "at startup");
        Assert.DoesNotThrowAsync(() => RequestOneSlotByRangeAsync(pool, slotClock, afterHeadStep), "after the head step");
    }

    [Test]
    public async Task Status_advertises_the_first_epoch_whose_checkpoint_is_the_anchor_block([Values(0UL, 1UL, 31UL)] ulong slotInEpoch)
    {
        ulong anchorSlot = FuluAnchorSlot + slotInEpoch;
        ulong expectedEpoch = FuluAnchorSlot / Spec.SlotsPerEpoch + (slotInEpoch == 0 ? 0UL : 1UL);
        CheckpointRef storeFinalized = new(Spec.GetEpoch(anchorSlot), TestChain.BuildLinkedChain(anchorSlot).AnchorRoot);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(new DataColumnSidecarPool(), anchorSlot, FuluWallSlot, finalized: storeFinalized);
        ulong atStartup = statusHolder.CurrentStatus.FinalizedEpoch;

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(FuluAnchorSlot % Spec.SlotsPerEpoch, Is.Zero, "fixture: the anchor offset is its slot in the epoch");
        Assert.That(atStartup, Is.EqualTo(expectedEpoch), "at startup");
        Assert.That(statusHolder.CurrentStatus.FinalizedEpoch, Is.EqualTo(expectedEpoch), "after the head step, while fork choice still holds the anchor as finalized");
        Assert.That(statusHolder.CurrentStatus.FinalizedRoot, Is.EqualTo(storeFinalized.Root));
    }

    [Test]
    public async Task Head_step_publishes_the_head_root_only_while_the_head_is_full([Values] bool full)
    {
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(new DataColumnSidecarPool(), FuluAnchorSlot, FuluWallSlot, headFull: full);
        statusHolder.Publish(statusHolder.CurrentStatus, TestItem.KeccakB);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentHead.FullHeadRoot, Is.EqualTo(full ? TestItem.KeccakA : null), "a stale FULL root must not survive a head that is EMPTY");
    }

    [Test]
    public async Task Production_wiring_advertises_the_earliest_slot_from_the_shared_column_pool()
    {
        ManualTimestamper timestamper = new(WallTime(FuluWallSlot));
        using IContainer container = BeaconChainTestContainer.Builder()
            .AddSingleton<ITimestamper>(timestamper)
            .Build();
        BeaconSyncOrchestrator orchestrator = container.Resolve<BeaconSyncOrchestrator>();
        BeaconChainStatusHolder statusHolder = container.Resolve<BeaconChainStatusHolder>();
        AddColumn(container.Resolve<DataColumnSidecarPool>(), ServeRangeStart + 10);
        // The store records its floor with the first write, which the module's writer runs off this thread.
        DataColumnSidecarPoolPersistenceTests.DrainStoreWrites(container.Resolve<ColumnStoreWriter>());

        Initialize(orchestrator, ImporterWithHead(FuluAnchorSlot, FuluHeadOffset), FuluAnchorSlot);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(ServeRangeStart + 10), "the orchestrator must read the pool the column protocols serve from");
    }

    private static (BeaconSyncOrchestrator Orchestrator, BeaconChainStatusHolder StatusHolder, SlotClock SlotClock) CreateStatusHarness(DataColumnSidecarPool pool, ulong anchorSlot, ulong wallSlot, ulong headOffset = FuluHeadOffset, bool headFull = false, BeaconChainSpec? forkSpec = null, ulong? backfilledFrom = null, CheckpointRef? finalized = null)
    {
        BeaconChainSpec spec = forkSpec ?? Spec;
        ManualTimestamper timestamper = new(WallTime(wallSlot));
        SlotClock slotClock = new(spec, timestamper);
        StubPool peers = new([]);
        ScriptedImporter importer = ImporterWithHead(anchorSlot, headOffset, headFull, finalized);
        BeaconChainStatusHolder statusHolder = new(spec, timestamper);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        RangeSync rangeSync = new(peers, LimboLogs.Instance, pool, spec, RangeSyncTests.ClockAtGenesis(spec));
        ColumnBackfill? columnBackfill = backfilledFrom is { } from
            ? new ColumnBackfill(store, pool, rangeSync, peers, slotClock, spec, statusHolder, new DiscoveryNodeCustodySource(null), LimboLogs.Instance) { CompleteFrom = from }
            : null;
        BeaconSyncOrchestrator orchestrator = new(
            new BeaconChainConfig(),
            spec,
            store,
            new ScriptedFactory(importer),
            new ScriptedEngine(),
            peers,
            rangeSync,
            slotClock,
            new GossipRouter(spec, slotClock, LimboLogs.Instance),
            statusHolder,
            LimboLogs.Instance,
            columnPool: pool,
            columnBackfill: columnBackfill);
        Initialize(orchestrator, importer, anchorSlot);
        return (orchestrator, statusHolder, slotClock);
    }

    private static ScriptedImporter ImporterWithHead(ulong anchorSlot, ulong headOffset, bool headFull = false, CheckpointRef? finalized = null) => new()
    {
        Head = new HeadView(TestItem.KeccakA, anchorSlot + headOffset, null, null, null, new CheckpointRef(0, TestItem.KeccakC), finalized ?? new CheckpointRef(0, TestItem.KeccakD), headFull),
    };

    private static void Initialize(BeaconSyncOrchestrator orchestrator, ScriptedImporter importer, ulong anchorSlot)
    {
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(anchorSlot);
        orchestrator.Initialize(importer, new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot);
        orchestrator.GossipStarted = true;
    }

    private static DateTime WallTime(ulong slot) => DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + slot * Spec.SecondsPerSlot).AddSeconds(6);

    private static void AddColumn(DataColumnSidecarPool pool, ulong slot) =>
        pool.Add(Keccak.Compute(BitConverter.GetBytes(slot)), slot, new DataColumnSidecar
        {
            Index = 0,
            Column = [],
            KzgCommitments = [],
            KzgProofs = [],
            SignedBlockHeader = new SignedBeaconBlockHeader { Message = new BeaconBlockHeader { Slot = slot, ParentRoot = Hash256.Zero, StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero } },
            KzgCommitmentsInclusionProof = [.. new Hash256[Eip7594DasConstants.KzgCommitmentsInclusionProofDepth].Select(static _ => Hash256.Zero)],
        });

    private static async Task RequestOneSlotByRangeAsync(DataColumnSidecarPool pool, SlotClock slotClock, ulong startSlot)
    {
        DataColumnSidecarsByRangeProtocol protocol = new(Spec, pool, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), slotClock);
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State());
        Channel channel = new();
        Task listen = ListenThenCloseAsync();
        await protocol.DialAsync(channel, context, new(new DataColumnSidecarsByRangeRequest { StartSlot = startSlot, Count = 1, Columns = [0] }, Gloas: false));
        await listen;

        async Task ListenThenCloseAsync()
        {
            await protocol.ListenAsync(channel.Reverse, context);
            await channel.Reverse.WriteEofAsync();
        }
    }
}
