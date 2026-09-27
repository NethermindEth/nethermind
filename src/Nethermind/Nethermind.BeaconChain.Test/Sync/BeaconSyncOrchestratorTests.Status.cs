// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    // Epoch 412,500 on mainnet: past FuluForkEpoch 411,392.
    private const ulong FuluAnchorSlot = 13_200_000;
    private const ulong FuluHeadOffset = 64;
    // A node that has followed the chain for a while: the wall clock is 4,196 epochs past the anchor, so the 4,096-epoch
    // data_column_serve_range starts 100 epochs (3,200 slots) above it and the node holds every block of that range.
    private const ulong ServeRangeStart = FuluAnchorSlot + 100 * 32;
    private const ulong FuluWallSlot = FuluAnchorSlot + (4096 + 100) * 32 + 5;
    // A node that started recently: the anchor is inside the serve range, so some blocks of it are not held.
    private const ulong YoungWallSlot = FuluAnchorSlot + 100;

    /// <summary>fulu/p2p-interface.md Status v2: a node holding every block of the sidecar retention period advertises the earliest slot from which it can serve all sidecars.</summary>
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

    /// <summary>fulu/p2p-interface.md Status v2: the sidecar exception needs every block of the retention period; a node whose anchor is inside it advertises the anchor.</summary>
    [Test]
    public async Task A_node_missing_blocks_of_the_serve_range_advertises_its_anchor()
    {
        DataColumnSidecarPool pool = new();
        AddColumn(pool, FuluAnchorSlot + 10);
        (BeaconSyncOrchestrator orchestrator, BeaconChainStatusHolder statusHolder, _) = CreateStatusHarness(pool, FuluAnchorSlot, YoungWallSlot);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(FuluAnchorSlot));
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(ServeRangeStart + 1));
            Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(ServeRangeStart + 2), "the first slot of the range lost its column");
        }
    }

    /// <summary>
    /// Before Fulu no peer asks for columns, so the anchor is still the earliest available block. From Fulu the serve range
    /// starts at the fork, so an anchor below it holds every block of the range and the held columns bound the slot.
    /// </summary>
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

    /// <summary>fulu/p2p-interface.md Status v2: a by-range request from the advertised slot must not be refused, whether sent at startup or after a head step.</summary>
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
    public async Task Production_wiring_advertises_the_earliest_slot_from_the_shared_column_pool()
    {
        ManualTimestamper timestamper = new(WallTime(FuluWallSlot));
        using IContainer container = BeaconChainTestContainer.Builder()
            .AddSingleton<ITimestamper>(timestamper)
            .Build();
        BeaconSyncOrchestrator orchestrator = container.Resolve<BeaconSyncOrchestrator>();
        BeaconChainStatusHolder statusHolder = container.Resolve<BeaconChainStatusHolder>();
        AddColumn(container.Resolve<DataColumnSidecarPool>(), ServeRangeStart + 10);
        Initialize(orchestrator, ImporterWithHead(FuluAnchorSlot, FuluHeadOffset), FuluAnchorSlot);

        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(statusHolder.CurrentStatus.EarliestAvailableSlot, Is.EqualTo(ServeRangeStart + 10), "the orchestrator must read the pool the column protocols serve from");
    }

    private static (BeaconSyncOrchestrator Orchestrator, BeaconChainStatusHolder StatusHolder, SlotClock SlotClock) CreateStatusHarness(DataColumnSidecarPool pool, ulong anchorSlot, ulong wallSlot, ulong headOffset = FuluHeadOffset)
    {
        ManualTimestamper timestamper = new(WallTime(wallSlot));
        SlotClock slotClock = new(Spec, timestamper);
        StubPool peers = new([]);
        ScriptedImporter importer = ImporterWithHead(anchorSlot, headOffset);
        BeaconChainStatusHolder statusHolder = new(Spec, timestamper);
        BeaconSyncOrchestrator orchestrator = new(
            new BeaconChainConfig(),
            Spec,
            new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            new ScriptedFactory(importer),
            new ScriptedEngine(),
            peers,
            new RangeSync(peers, LimboLogs.Instance, pool, Spec, RangeSyncTests.ClockAtGenesis(Spec)),
            slotClock,
            new GossipRouter(Spec, slotClock, LimboLogs.Instance),
            statusHolder,
            LimboLogs.Instance,
            columnPool: pool);
        Initialize(orchestrator, importer, anchorSlot);
        return (orchestrator, statusHolder, slotClock);
    }

    // No execution hash, so the head step never reaches the engine.
    private static ScriptedImporter ImporterWithHead(ulong anchorSlot, ulong headOffset) => new()
    {
        Head = new HeadView(TestItem.KeccakA, anchorSlot + headOffset, null, null, null, new CheckpointRef(0, TestItem.KeccakC), new CheckpointRef(0, TestItem.KeccakD)),
    };

    private static void Initialize(BeaconSyncOrchestrator orchestrator, ScriptedImporter importer, ulong anchorSlot)
    {
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(anchorSlot);
        orchestrator.Initialize(importer, anchorBlock, anchorRoot);
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
            SignedBlockHeader = new SignedBeaconBlockHeader { Message = new BeaconBlockHeader { Slot = slot } },
            KzgCommitmentsInclusionProof = [],
        });

    // The listen side closes the stream once it has answered, as the libp2p host does.
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
