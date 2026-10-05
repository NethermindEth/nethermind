// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Libp2p.Protocols.Pubsub;
using NSubstitute;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    private const ulong BlockScenarioNearWall = WallSlot - 5;
    [Test]
    public async Task Deferred_block_is_retried_as_requested_only_when_this_node_fetched_it([Values] BlockSource source)
    {
        BlockContext s = BackfillBlocks(BlockScenarioNearWall, 2);
        s.Serve(0);
        s.Importer.Unavailable.Add(s.Root(0));
        switch (source)
        {
            case BlockSource.Gossip:
                await s.Import(0);
                break;
            case BlockSource.RangeSync:
                s.Queue(0);
                s.Queue(1);
                await s.Drain();
                break;
            case BlockSource.ByRootBackfill:
                await s.Walk(1);
                break;
            case BlockSource.GossipThenFetched:
                await s.Import(0);
                await s.Import(0, fetched: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        bool waited = s.Sync.PendingRetryBlockCount == 1 && !s.Importer.Known.Contains(s.Root(0));
        s.Importer.Unavailable.Remove(s.Root(0));
        await s.Tick(BlockScenarioNearWall + 3);
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(waited, Is.True);
        s.Known(0);
        s.Imports(0, source == BlockSource.Gossip ? 0 : 2, requested: true);
        if (source == BlockSource.RangeSync)
        {
            s.Known(1);
            s.Imports(1, 2, requested: true);
        }
    }

    [Test]
    public async Task Peer_serving_an_invalid_block_by_root_is_blamed([Values] bool onRetry)
    {
        BlockContext s = BackfillBlocks(BlockScenarioNearWall, 2);
        s.Serve(0);
        s.Importer.Forged.Add(s.Root(0));
        if (onRetry)
            s.Importer.Unavailable.Add(s.Root(0));
        await s.Walk(1);
        if (onRetry)
        {
            s.NoBlame();
            s.Importer.Unavailable.Remove(s.Root(0));
            await s.Tick(BlockScenarioNearWall + 3);
        }

        s.Blame();
    }

    [Test]
    public async Task Forged_copy_drained_before_the_genuine_block_keeps_its_children()
    {
        BlockContext s = BackfillBlocks(BlockScenarioNearWall, 3);
        s.Copy(1, 3, 0x11, forged: true);
        s.ServeEach(0, 1);
        s.Importer.RegenerationRefused.Add(s.Root(0));
        await s.Walk(3);
        await s.Walk(2);
        int held = s.Sync.PendingGossipBlockCount;
        s.Importer.RegenerationRefused.Remove(s.Root(0));
        await s.Tick(BlockScenarioNearWall + 4);
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(held, Is.EqualTo(3));
        s.Known(1, 2);
    }

    [Test]
    public async Task Peer_serving_a_block_by_root_is_blamed_only_when_its_data_is_invalid([Values] bool localAdmission)
    {
        BlockContext s = BackfillBlocks(BlockScenarioNearWall, 1);
        (localAdmission ? s.Importer.AdmissionRefused : s.Importer.Forged).Add(s.Root(0));
        BlockImportResult result = await s.Import(0, fetched: true, supplier: s.Peer);
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        s.Blame(localAdmission ? 0 : 1);
    }

    [TestCase(false, TestName = "Forged_gossip_copy_of_a_block_fetched_by_root_does_not_blame_its_supplier")]
    [TestCase(true, TestName = "Invalid_copy_of_a_waiting_fetched_block_keeps_its_held_child")]
    public async Task Invalid_gossip_copy_preserves_the_fetched_block(bool waitingForRegeneration)
    {
        BlockContext s = BackfillBlocks(BlockScenarioNearWall, 2);
        s.Serve(0);
        s.Copy(0, 2, 0x11, forged: waitingForRegeneration);
        HashSet<Hash256> wait = waitingForRegeneration ? s.Importer.RegenerationRefused : s.Importer.Unavailable;
        wait.Add(s.Root(0));
        await s.Walk(1);
        wait.Remove(s.Root(0));
        if (!waitingForRegeneration)
            s.Importer.Forged.Add(s.Root(0));
        BlockImportResult result = await s.Import(2);
        if (waitingForRegeneration)
            await s.Tick(BlockScenarioNearWall + 3);
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        if (waitingForRegeneration)
            s.Known(0, 1);
        else
        {
            s.Pending(retries: 1);
            s.Imports(0, 1, byRoot: true);
            s.NoBlame();
        }
    }

    [Test]
    public async Task Fetched_block_held_behind_a_deferred_fetched_ancestor_blames_its_supplier_when_invalid()
    {
        BlockContext s = BackfillBlocks(BlockScenarioNearWall, 3);
        s.ServeEach(0, 1);
        s.Importer.RegenerationRefused.Add(s.Root(0));
        s.Importer.Forged.Add(s.Root(1));
        await s.Walk(2);
        bool waited = s.Sync.PendingRetryBlockCount == 1;
        s.Importer.RegenerationRefused.Remove(s.Root(0));
        await s.Tick(BlockScenarioNearWall + 4);
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(waited, Is.True);
        s.Known(0);
        s.Imports(1, byRoot: true);
        s.Blame();
    }

    [Test]
    public async Task Fetched_chain_on_an_unregenerated_parent_is_held_only_for_a_budget_refusal_and_only_up_to_the_cap([Values] bool budgetRefusal)
    {
        BlockContext s = BackfillBlocks(WallSlot - 31, 31);
        s.ServeRequestedRoots();
        (budgetRefusal ? s.Importer.RegenerationRefused : s.Importer.RegenerationImpossible).Add(s.Root(0));
        int before = s.Sync.PendingGossipBlockCount;
        await s.Walk(30);
        using IDisposable assertions = Assert.EnterMultipleScope();
        s.Imports(0);
        s.Pending(retries: budgetRefusal ? 1 : 0);
        Assert.That(s.Sync.PendingGossipBlockCount - before, Is.EqualTo(budgetRefusal ? BeaconSyncOrchestrator.MaxHeldRefusedBackfills : 0));
    }

    [Test]
    public async Task Descendants_held_behind_a_budget_deferred_fetched_block_import_on_the_next_slot()
    {
        BlockContext s = BackfillBlocks(WallSlot - 21, 21);
        s.ServeRequestedRoots();
        s.Importer.RegenerationRefused.Add(s.Root(0));
        await s.Walk(20);
        s.Importer.RegenerationRefused.Remove(s.Root(0));
        await s.Tick(WallSlot);
        using IDisposable assertions = Assert.EnterMultipleScope();
        s.Known(0);
        Assert.That(Enumerable.Range(1, BeaconSyncOrchestrator.MaxHeldRefusedBackfills).Select(s.Root), Is.SubsetOf(s.Importer.Known));
        s.Pending(gossip: 0);
    }

    [Test]
    public async Task Gossip_child_of_a_parent_waiting_on_its_payload_imports_after_the_envelope([Values] bool parentAlreadyHeld)
    {
        BlockContext s = ParkedBlocks();
        s.Importer.OnImported = (block, root) => s.Importer.Head = CreateHead(root, block.Slot, Spec.GetEpoch(NearHeadAnchorSlot));
        if (parentAlreadyHeld)
            await s.Walk(0);
        await s.Walk(1);
        await s.Envelope();
        await s.Walk(2);
        await s.Sync.ProcessQueuedAsync(CancellationToken.None);
        using IDisposable assertions = Assert.EnterMultipleScope();
        s.Known(1, 2);
        s.Tip(2);
        s.Seen(2, true);
        Assert.That(ByRootRequests(s.Peer), Is.EqualTo(parentAlreadyHeld ? 0 : 1));
        s.Pending(gossip: 0);
    }

    [Test]
    public async Task Child_held_for_a_parked_block_is_released_when_that_block_can_no_longer_import([Values] ParkedBlockFate fate)
    {
        BlockContext s = ParkedBlocks();
        s.Sync.GossipStarted = true;
        await s.Walk(0);
        await s.Walk(1);
        await s.Walk(2);
        int before = s.Sync.PendingGossipBlockCount;
        switch (fate)
        {
            case ParkedBlockFate.FinalizedAway:
                s.Importer.Head = CreateHead(TestItem.KeccakA, WallSlot, Spec.GetEpoch(BlockScenarioNearWall + 2) + 1);
                break;
            case ParkedBlockFate.RetryInvalid:
                s.Importer.UnverifiedPayloads.Remove(s.Root(-1));
                s.Importer.Forged.Add(s.Root(0));
                break;
            case ParkedBlockFate.RetryUnknownParent:
                s.Importer.Known.Remove(s.Root(-1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fate));
        }

        await s.Tick(WallSlot);
        int after = s.Sync.PendingGossipBlockCount;
        s.AddGloasChild(1, 3, BlockScenarioNearWall + 5);
        await s.Walk(3);
        bool kept = fate != ParkedBlockFate.FinalizedAway;
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(before, Is.EqualTo(2));
        Assert.That(after, Is.Zero);
        s.Requests(1, kept ? 1 : 0);
        s.Pending(gossip: kept ? 1 : 0);
    }

    [Test]
    public async Task Descendants_of_a_held_child_are_held_without_fetching_it_again([Values] bool parentUnknown)
    {
        BlockContext s = ParkedBlocks();
        s.AddGloasChild(2, 3, BlockScenarioNearWall + 5);
        s.ServeEach(1, 2);
        await s.Walk(0);
        await s.Walk(1);
        await s.Walk(parentUnknown ? 3 : 2);
        int held = s.Sync.PendingGossipBlockCount;
        await s.Envelope();
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(held, Is.EqualTo(parentUnknown ? 3 : 2));
        s.Requests(1, 0);
        s.Requests(2, parentUnknown ? 1 : 0);
        s.Known(2);
        Assert.That(s.Importer.Known.Contains(s.Root(3)), Is.EqualTo(parentUnknown));
        s.Pending(gossip: 0);
        s.Imports(2, parentUnknown ? null : 0, byRoot: true);
        s.Imports(3, 0, requested: true);
    }

    [Test]
    public async Task Fetched_block_held_for_a_parent_payload_is_released_as_fetched_and_blames_its_supplier_when_invalid()
    {
        BlockContext s = ParkedBlocks();
        s.ServeEach(1);
        s.Importer.InvalidTransition.Add(s.Root(1));
        await s.Walk(2);
        int held = s.Sync.PendingGossipBlockCount;
        await s.Envelope();
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(held, Is.EqualTo(2));
        s.Known(0);
        s.Imports(1, 2, byRoot: true);
        s.Blame();
    }

    [Test]
    public async Task Forged_child_of_a_parked_block_is_not_held([Values] bool farBehind)
    {
        BlockContext s = ParkedBlocks(farBehind);
        s.Importer.Forged.Add(s.Root(1));
        await s.Gossip(0);
        int calls = s.Peer.ReceivedCalls().Count();
        await s.Gossip(1);
        using IDisposable assertions = Assert.EnterMultipleScope();
        s.Pending(gossip: 0);
        s.Seen(1, false);
        Assert.That(s.Peer.ReceivedCalls().Count(), Is.EqualTo(calls));
    }

    [Test]
    public async Task Child_of_a_parked_block_the_importer_no_longer_defers_is_not_held()
    {
        BlockContext s = ParkedBlocks();
        await s.Gossip(0);
        s.Importer.Known.Remove(s.Root(-1));
        Assert.That(s.Importer.Import(s[0], s.Root(0), verifySignatures: true), Is.EqualTo(BlockImportResult.UnknownParent));
        s.Importer.Known.Add(s.Root(-1));
        await s.Gossip(1);
        using IDisposable assertions = Assert.EnterMultipleScope();
        s.Pending(gossip: 0);
        s.Seen(1, false);
    }

    [Test]
    public async Task Fetched_copy_does_not_lend_its_supplier_to_a_queued_forgery([Values] CopyArrival arrival)
    {
        BlockContext s = BackfillBlocks(BlockScenarioNearWall, 2);
        s.Copy(0, 2, 0x11, forged: true);
        s.Serve();
        s.Importer.Unavailable.Add(s.Root(0));
        if (arrival != CopyArrival.GenuineFetchedFirst)
        {
            await s.Import(2);
        }

        if (arrival == CopyArrival.GenuineFromGossipThenFetched)
        {
            await s.Import(0);
        }

        await s.Import(0, fetched: true, supplier: s.Peer);
        if (arrival == CopyArrival.GenuineFetchedFirst)
        {
            await s.Import(2);
        }

        int waiting = s.Sync.PendingRetryBlockCount;
        await s.Walk(1);
        int heldChildren = s.Sync.PendingGossipBlockCount;
        s.Importer.Unavailable.Remove(s.Root(0));
        await s.Tick(BlockScenarioNearWall + 2);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(waiting, Is.EqualTo(2), "both copies wait for their data");
        Assert.That(heldChildren, Is.EqualTo(1), "a gossip child is held for the root");
        s.Known(0, 1);
        s.Pending(retries: 0);
        s.Imports(0, count: 2, byRoot: true);
        s.NoBlame();
    }

    [Test]
    public async Task Queued_gossip_forgery_of_a_held_range_block_does_not_blame_the_range_peer_or_drop_the_held_chain()
    {
        BlockContext s = QuietBlocks(BlockScenarioNearWall, 2);
        s.Copy(0, 2, 0x11, forged: true);
        s.Importer.Unavailable.Add(s.Root(0));
        await s.Import(2);
        s.Queue(0, s.Peer);
        s.Queue(1, s.Peer);
        await s.Drain();
        ulong? heldBefore = s.Sync.RangeHeldSlot;
        s.Importer.Unavailable.Remove(s.Root(0));
        s.Importer.EngineDown.Add(s.Root(0));
        await s.Tick(WallSlot);
        ulong? heldAfterForgery = s.Sync.RangeHeldSlot;
        s.Importer.EngineDown.Remove(s.Root(0));
        s.Importer.RequestedImports.Clear();
        await s.Tick(WallSlot + 1);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldBefore, Is.EqualTo(s[1].Slot), "the range chain is held behind the queued root");
        Assert.That(heldAfterForgery, Is.EqualTo(s[1].Slot), "the forgery leaves the held chain and its round");
        s.NoBlame();
        s.Known(0, 1);
        s.Imports(1, requested: true);
    }

    [Test]
    public async Task Promoted_range_copy_from_a_second_range_peer_blames_that_peer()
    {
        BlockContext s = QuietBlocks(BlockScenarioNearWall, 1);
        s.Copy(0, 1, 0x11, forged: true);
        s.Copy(0, 2, 0x22, forged: true);
        IBeaconSyncPeer secondPeer = Substitute.For<IBeaconSyncPeer>();
        s.Importer.Unavailable.Add(s.Root(0));
        s.Queue(1, s.Peer);
        s.Queue(2, secondPeer);
        await s.Drain();
        int waiting = s.Sync.PendingRetryBlockCount;
        s.Importer.Unavailable.Remove(s.Root(0));
        await s.Tick(WallSlot);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(waiting, Is.EqualTo(2), "both signed copies wait for their data");
        s.Blame();
        secondPeer.Received(1).ReportFailure(PeerFailureReason.ProtocolViolation, Arg.Any<string>());
        s.Pending(retries: 0);
    }

    [Test]
    public async Task Walk_whose_ancestor_is_queued_while_its_fetch_runs_imports_with_the_genuine_copy([Values] bool genuineWaitsForRegeneration)
    {
        BlockContext s = BackfillBlocks(BlockScenarioNearWall, 2);
        s.Copy(0, 2, 0x11, forged: true);
        s.Serve(2);
        IBeaconSyncPeer supplier = Substitute.For<IBeaconSyncPeer>();
        s.Importer.Unavailable.Add(s.Root(0));
        await s.Gossip(1);
        await s.Import(2);
        await s.Import(0, fetched: true, supplier: supplier);
        s.Importer.Unavailable.Remove(s.Root(0));
        if (genuineWaitsForRegeneration)
        {
            s.Importer.RegenerationRefused.Add(s.Root(0));
        }

        await s.Sync.SettleWithinAsync(maxPasses: 10, CancellationToken.None);
        int held = s.Sync.PendingGossipBlockCount;
        await s.Tick(WallSlot);
        bool importedInFirstSlot = s.Importer.Known.Contains(s.Root(0));
        s.Importer.RegenerationRefused.Remove(s.Root(0));
        await s.Tick(WallSlot + 1);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(held, Is.EqualTo(1), "the child is held for the queued root");
        Assert.That(importedInFirstSlot, Is.EqualTo(!genuineWaitsForRegeneration));
        s.Known(0, 1);
        s.Imports(0, count: genuineWaitsForRegeneration ? 5 : 4);
        supplier.DidNotReceive().ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string>());
    }

    [Test]
    public async Task Child_of_a_parked_block_is_held_once_per_slot_and_proposer([Values] bool equivocation)
    {
        BlockContext s = ParkedBlocks();
        SignedBeaconBlockGloas equivocating = CreateMinimalGloasBlock(s[1].Slot, s.Root(0));
        equivocating.Message!.Body!.Graffiti = TestItem.KeccakB;
        ForkedSignedBeaconBlock repeat = equivocation ? new ForkedSignedBeaconBlock.OfGloas(equivocating) : s[1];
        await s.Gossip(0);
        await s.Gossip(1);
        await s.Sync.ProcessGossipBlockAsync(repeat, CancellationToken.None);
        s.Pending(gossip: 1);
    }

    [Test]
    public async Task Fetched_ancestor_of_a_parked_block_is_not_held_for_a_seen_slot_and_proposer()
    {
        BlockContext s = ParkedBlocks();
        s.ServeEach(1);
        await s.Gossip(0);
        s.Harness.Router.MarkProposalSeen(s[1].Slot, s[1].ProposerIndex);
        await s.Gossip(2);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        s.Requests(1, 1);
        s.Pending(gossip: 0);
    }

    [Test]
    public async Task Fetched_ancestor_of_a_parked_block_failing_its_first_import_blames_its_supplier_only_when_invalid([Values] HoldCheckFailure failure)
    {
        BlockContext s = ParkedBlocks();
        s.ServeEach(1);
        await s.Gossip(0);
        switch (failure)
        {
            case HoldCheckFailure.Invalid:
                s.Importer.Forged.Add(s.Root(1));
                break;
            case HoldCheckFailure.LocalAdmission:
                s.Importer.AdmissionRefused.Add(s.Root(1));
                break;
            case HoldCheckFailure.ParentForgotten:
                s.Importer.Known.Remove(s.Root(-1));
                s.Importer.Import(s[0], s.Root(0), verifySignatures: true);
                s.Importer.Known.Add(s.Root(-1));
                break;
        }

        await s.Walk(2);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        s.Requests(1, 1);
        s.Imports(1, byRoot: true);
        s.Pending(gossip: 0);
        s.Blame(failure == HoldCheckFailure.Invalid ? 1 : 0);
    }

    [Test]
    public async Task Genuine_ancestor_fetched_while_a_forgery_of_it_waits_imports_with_the_walk([Values] bool genuineAlreadyWaiting)
    {
        (BlockContext s, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = WaitingBlocks(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        s.Copy(0, 2, 0x11, forged: true);
        s.Importer.Unavailable.Add(s.Root(0));
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        s.Token = cts.Token;
        await s.Gossip(1);
        await s.Import(2);
        if (genuineAlreadyWaiting)
        {
            await s.Import(0, fetched: true);
        }

        fetches[s.Root(0)].SetResult([s[0]]);
        await s.Sync.SettleWithinAsync(maxPasses: 10, cts.Token);
        int waiting = s.Sync.PendingRetryBlockCount;
        s.Importer.Unavailable.Remove(s.Root(0));
        await s.Tick(WallSlot);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(waiting, Is.EqualTo(2), "the fetched copy waits beside the forgery");
        s.Known(0, 1);
        s.Imports(0, count: 2, byRoot: true);
    }

    [Test]
    public async Task Ancestor_queued_from_gossip_and_fetched_while_waiting_retries_as_fetched([Values] bool behindForgery)
    {
        (BlockContext s, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = WaitingBlocks(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        s.Importer.Unavailable.Add(s.Root(0));
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        s.Token = cts.Token;
        s.Copy(0, 2, 0x11, forged: true);
        await s.Gossip(1);
        if (behindForgery)
        {
            await s.Import(2);
        }

        await s.Import(0);
        fetches[s.Root(0)].SetResult([s[0]]);
        await s.Sync.SettleWithinAsync(maxPasses: 10, cts.Token);
        s.Importer.Unavailable.Remove(s.Root(0));
        s.Importer.RegenerationRefused.Add(s.Root(0));
        await s.Tick(WallSlot);
        s.Importer.RegenerationRefused.Remove(s.Root(0));
        await s.Tick(WallSlot + 1);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        s.Known(0, 1);
        s.Imports(0, byRoot: true);
    }

    [Test]
    public async Task Child_of_a_fulu_block_waiting_on_its_data_imports_with_its_retry_without_fetching_it([Values] bool fetchedIntermediate)
    {
        BlockContext s = BackfillBlocks(BlockScenarioNearWall, 3);
        s.Serve(0, 1);
        s.Importer.Unavailable.Add(s.Root(0));
        BlockImportResult parked = await s.Import(0);
        await s.Walk(fetchedIntermediate ? 2 : 1);
        int held = s.Sync.PendingGossipBlockCount;
        s.Importer.Unavailable.Remove(s.Root(0));
        await s.Tick(WallSlot);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(parked, Is.EqualTo(BlockImportResult.DataUnavailable));
        s.Requests(0, 0);
        Assert.That(held, Is.EqualTo(fetchedIntermediate ? 2 : 1));
        s.Known(0, 1, fetchedIntermediate ? 2 : 1);
        Assert.That(s.Importer.ByRootImports, fetchedIntermediate ? Is.EqualTo(new[] { s.Root(1) }) : Is.Empty);
    }

    [Test]
    public async Task Child_of_a_parked_block_the_full_queue_cannot_take_is_not_marked_seen()
    {
        const int PendingQueueCapacity = 128;
        BlockContext s = ParkedBlocks(farBehind: true);
        await s.Gossip(0);
        for (int i = 0; i < PendingQueueCapacity; i++)
        {
            ForkedSignedBeaconBlock filler = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(s[2].Slot + 1 + (ulong)i, TestItem.Keccaks[i]));
            await s.Sync.ProcessGossipBlockAsync(filler, CancellationToken.None);
        }

        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();
        await s.Sync.ProcessGossipBlockAsync(s[1], CancellationToken.None, verdict);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        s.Pending(gossip: PendingQueueCapacity);
        Assert.That(given, Is.EqualTo(new[] { MessageValidity.Ignored }), "the verdict precedes any slot tick");
        s.Seen(1, false);
        Assert.That(s.Importer.Imports.Any(import => import.Root == s.Root(1)), Is.False);
    }

    [Test]
    public async Task Walk_holds_its_blocks_behind_a_fetched_ancestor_waiting_for_a_retry([Values] RetryCause cause)
    {
        BlockContext s = BackfillBlocksAt(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        s.Serve(0);
        HashSet<Hash256> waitsFor = cause switch
        {
            RetryCause.Data => s.Importer.Unavailable,
            RetryCause.Engine => s.Importer.EngineDown,
            _ => s.Importer.Early,
        };
        waitsFor.Add(s.Root(0));
        if (cause == RetryCause.Slot)
        {
            s.Importer.Ticks.Clear();
            s.Importer.Ticks.Add(s[0].Slot - 1);
        }

        await s.Walk(1);
        int held = s.Sync.PendingGossipBlockCount;
        waitsFor.Remove(s.Root(0));
        await s.Tick(WallSlot);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(held, Is.EqualTo(1));
        s.Known(0, 1);
        s.Imports(0, byRoot: true);
    }

    [Test]
    public async Task Copies_of_a_queued_block_count_towards_the_retry_set_bound([Values] bool copyLast)
    {
        const int RetrySetCapacity = 128;
        BlockContext s = QuietBlocks(BlockScenarioNearWall, 1);
        s.Copy(0, 1, 0x11);
        s.Importer.Unavailable.Add(s.Root(0));
        await s.Import(1);
        if (!copyLast)
        {
            await s.Import(0);
        }

        await s.FillRetrySet(RetrySetCapacity);
        ForkedSignedBeaconBlock last = copyLast ? s[0] : new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(WallSlot + 1 + RetrySetCapacity, s.FuluAnchorRoot));
        s.Importer.Unavailable.Add(last.ComputeMessageRoot());
        await s.Sync.ImportBlockAsync(last, CancellationToken.None);
        s.Pending(retries: RetrySetCapacity);
    }

    [Test]
    public async Task Child_held_for_a_block_the_full_retry_set_refuses_is_released()
    {
        BlockContext s = QuietBlocksAt(AnchorSlot, WallSlot - 2, WallSlot - 1);
        await s.Gossip(1);
        int heldBefore = s.Sync.PendingGossipBlockCount;
        await s.FillRetryAttempts(128);
        s.Importer.Unavailable.Add(s.Root(0));
        await s.Import(0);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldBefore, Is.EqualTo(1));
        s.Pending(gossip: 0);
    }

    [Test]
    public async Task Children_held_for_a_block_with_an_unknown_parent_import_with_it()
    {
        BlockContext s = QuietBlocksAt(AnchorSlot, WallSlot - 2, WallSlot - 1);
        s.Importer.Known.Remove(s.FuluAnchorRoot);
        await s.Gossip(1);
        BlockImportResult beforeAnchor = await s.Import(0);
        int heldAfterUnknownParent = s.Sync.PendingGossipBlockCount;
        s.Importer.Known.Add(s.FuluAnchorRoot);
        await s.Import(0);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(beforeAnchor, Is.EqualTo(BlockImportResult.UnknownParent));
        Assert.That(heldAfterUnknownParent, Is.EqualTo(1));
        s.Known(1);
        s.Pending(gossip: 0);
    }

    [Test]
    public async Task Head_imports_and_slot_ticks_proceed_while_an_ancestor_fetch_waits_and_the_chain_imports_in_order_once_it_arrives()
    {
        (BlockContext s, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = WaitingBlocks(NearHeadAnchorSlot, WallSlot - 3, WallSlot - 2, WallSlot - 1);
        ForkedSignedBeaconBlock[] blocks = [s[0], s[1], s[2]];
        ForkedSignedBeaconBlock head = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(WallSlot, s.FuluAnchorRoot));
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task gossip = s.Sync.ProcessGossipBlockAsync(blocks[2], cts.Token);
        bool gossipReturnedWhileFetchWaits = gossip.IsCompleted;
        await s.Sync.WorkWriter.WriteAsync(new BeaconSyncOrchestrator.GossipBlockItem(head), cts.Token);
        await s.Sync.WorkWriter.WriteAsync(new BeaconSyncOrchestrator.SlotTickItem(WallSlot), cts.Token);
        await s.Sync.ProcessQueuedAsync(cts.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gossipReturnedWhileFetchWaits, Is.True, "the worker does not wait for the by-root fetch");
            Assert.That(s.Importer.Known, Does.Contain(head.ComputeMessageRoot()), "a head block imports while the fetch waits");
            Assert.That(s.Importer.Ticks, Does.Contain(WallSlot), "a slot tick runs while the fetch waits");
            Assert.That(s.Sync.AncestorFetchesInFlight, Is.EqualTo(1));
        }

        fetches[blocks[1].ComputeMessageRoot()].SetResult([blocks[1]]);
        await WaitForFetchAsync(fetches, blocks[0].ComputeMessageRoot(), s.Harness, cts.Token);
        fetches[blocks[0].ComputeMessageRoot()].SetResult([blocks[0]]);
        await s.Sync.SettleWithinAsync(maxPasses: 10, cts.Token);
        await gossip;
        Hash256[] chainRoots = [.. blocks.Select(static b => b.ComputeMessageRoot())];
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(s.Importer.Imports.Select(static i => i.Root).Where(chainRoots.Contains), Is.EqualTo(chainRoots), "the fetched ancestors import oldest first, then the held gossip block");
        Assert.That(ByRootRequests(s.Peer), Is.EqualTo(2));
        Assert.That(s.Sync.AncestorFetchesInFlight, Is.Zero);
    }

    [Test]
    public async Task An_ancestor_being_fetched_is_not_requested_again([Values] bool deeperAncestorFetched)
    {
        (BlockContext s, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = WaitingBlocks(NearHeadAnchorSlot, WallSlot - 3, WallSlot - 2);
        if (!deeperAncestorFetched)
        {
            await s.Sync.ImportBlockAsync(s[0], CancellationToken.None);
        }

        ForkedSignedBeaconBlock first = ChildOf(s[1], WallSlot);
        ForkedSignedBeaconBlock second = ChildOf(s[1], WallSlot + 1);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await s.Sync.ProcessGossipBlockAsync(first, cts.Token);
        if (deeperAncestorFetched)
        {
            fetches[s[1].ComputeMessageRoot()].SetResult([s[1]]);
            await WaitForFetchAsync(fetches, s[0].ComputeMessageRoot(), s.Harness, cts.Token);
        }

        SetWallSlot(s.Harness, WallSlot + 1);
        await s.Sync.ProcessGossipBlockAsync(second, cts.Token);
        int requestsWhileWaiting = ByRootRequests(s.Peer);
        ForkedSignedBeaconBlock fetched = deeperAncestorFetched ? s[0] : s[1];
        fetches[fetched.ComputeMessageRoot()].SetResult([fetched]);
        CompleteOutstandingFetches(fetches);
        await s.Sync.SettleWithinAsync(maxPasses: 10, cts.Token);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(requestsWhileWaiting, Is.EqualTo(deeperAncestorFetched ? 2 : 1));
        Assert.That(s.Importer.Known, Does.Contain(first.ComputeMessageRoot()).And.Contain(second.ComputeMessageRoot()));
    }

    [Test]
    public async Task Gossip_block_imports_when_its_parent_arrives_elsewhere_while_the_fetch_waits([Values] bool parentWaitsForData)
    {
        (BlockContext s, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = WaitingBlocks(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        if (parentWaitsForData)
        {
            s.Importer.Unavailable.Add(s.Root(0));
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await s.Sync.ProcessGossipBlockAsync(s[1], cts.Token);
        await s.Sync.ImportBlockAsync(s[0], cts.Token);
        bool importedBeforeFetchEnded = s.Importer.Known.Contains(s[1].ComputeMessageRoot());
        int fetchesWhileWaiting = s.Sync.AncestorFetchesInFlight;
        fetches[s.Root(0)].SetResult(parentWaitsForData ? [s[0]] : []);
        await s.Sync.SettleWithinAsync(maxPasses: 10, cts.Token);
        int heldAfterFetch = s.Sync.PendingGossipBlockCount;
        s.Importer.Unavailable.Remove(s.Root(0));
        await s.Sync.ProcessSlotAsync(WallSlot, cts.Token);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(importedBeforeFetchEnded, Is.EqualTo(!parentWaitsForData), "the block does not wait for the fetch to end");
        Assert.That(heldAfterFetch, Is.EqualTo(parentWaitsForData ? 1 : 0), "the block waits for its parent's retry");
        Assert.That(fetchesWhileWaiting, Is.EqualTo(1), "the fetch still running keeps its place in the bound");
        Assert.That(s.Importer.Known, Does.Contain(s[1].ComputeMessageRoot()));
        Assert.That(s.Sync.PendingGossipBlockCount, Is.Zero, "nothing is held for a parent that already imported");
    }

    private static (BlockContext Context, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> Fetches) WaitingBlocks(ulong anchor, params ulong[] slots)
    {
        (_, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(anchor, slots);
        (Harness harness, IBeaconSyncPeer peer, Dictionary<Hash256, TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>>> fetches) = CreateWaitingByRootHarness();
        harness.Importer.Known.Add(anchorRoot);
        BlockContext context = new(harness, peer, chain.Select((block, index) => (index, Block: (ForkedSignedBeaconBlock)new ForkedSignedBeaconBlock.OfFulu(block))).ToDictionary(item => item.index, item => item.Block));
        return (context, fetches);
    }

    private static BlockContext QuietBlocks(ulong anchor, int count) =>
        QuietBlocksAt(anchor, [.. Enumerable.Range(1, count).Select(offset => anchor + (ulong)offset)]);

    private static BlockContext QuietBlocksAt(ulong anchor, params ulong[] slots)
    {
        (_, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(anchor, slots);
        Harness harness = CreateHarness(anchorSlot: anchor);
        harness.Importer.Known.Add(anchorRoot);
        return new(harness, Substitute.For<IBeaconSyncPeer>(), chain.Select((block, index) => (index, Block: (ForkedSignedBeaconBlock)new ForkedSignedBeaconBlock.OfFulu(block))).ToDictionary(item => item.index, item => item.Block));
    }

    private static BlockContext BackfillBlocks(ulong anchor, int count) =>
        BackfillBlocksAt(anchor, [.. Enumerable.Range(1, count).Select(offset => anchor + (ulong)offset)]);

    private static BlockContext BackfillBlocksAt(ulong anchor, params ulong[] slots)
    {
        (Harness harness, IBeaconSyncPeer peer, SignedBeaconBlock[] chain) = CreateBackfillChain(anchor, slots);
        return new(harness, peer, chain.Select((block, index) => (index, Block: (ForkedSignedBeaconBlock)new ForkedSignedBeaconBlock.OfFulu(block))).ToDictionary(item => item.index, item => item.Block));
    }

    private static BlockContext ParkedBlocks(bool farBehind = false)
    {
        ParkedParentScenario s = CreateParkedParentScenario(farBehind);
        return new(s.Harness, s.Peer, new() { [0] = s.Parent, [1] = s.Child, [2] = s.Grandchild }, s.FullRoot);
    }

    private sealed class BlockContext(Harness harness, IBeaconSyncPeer peer, Dictionary<int, ForkedSignedBeaconBlock> blocks, Hash256? fullRoot = null)
    {
        public Hash256 FuluAnchorRoot => ((ForkedSignedBeaconBlock.OfFulu)this[0]).Block.Message!.ParentRoot!;

        public async Task FillRetryAttempts(int attempts)
        {
            for (int i = 0; i < attempts; i++)
            {
                ForkedSignedBeaconBlock waiting = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(WallSlot + 1 + (ulong)i, FuluAnchorRoot));
                Importer.Unavailable.Add(waiting.ComputeMessageRoot());
                await Sync.ImportBlockAsync(waiting, Token);
            }
        }
        public async Task FillRetrySet(int capacity)
        {
            for (int i = 0; Sync.PendingRetryBlockCount < capacity; i++)
            {
                ForkedSignedBeaconBlock waiting = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(WallSlot + 1 + (ulong)i, FuluAnchorRoot));
                Importer.Unavailable.Add(waiting.ComputeMessageRoot());
                await Sync.ImportBlockAsync(waiting, Token);
            }
        }

        public CancellationToken Token { get; set; }
        public Harness Harness => harness;
        public ScriptedImporter Importer => harness.Importer;
        public BeaconSyncOrchestrator Sync => harness.Orchestrator;
        public IBeaconSyncPeer Peer => peer;
        public ForkedSignedBeaconBlock this[int index] => blocks[index];
        public Hash256 Root(int index) => index == -1 ? fullRoot! : blocks[index].ComputeMessageRoot();
        public Task<BlockImportResult> Import(int index, bool fetched = false, IBeaconSyncPeer? supplier = null) => Sync.ImportBlockAsync(this[index], Token, fetchedByRoot: fetched, servedBy: supplier);
        public Task Walk(int index) => Sync.ProcessGossipBlockAndFetchAncestorsAsync(this[index], Token);
        public Task Gossip(int index) => Sync.ProcessGossipBlockAsync(this[index], Token);
        public Task Tick(ulong slot) => Sync.ProcessSlotAsync(slot, Token);
        public Task Drain() => Sync.ProcessQueuedAsync(Token);
        public void Queue(int index, IBeaconSyncPeer? supplier = null) => Sync.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(this[index], supplier));
        public Task Envelope(int index = -1) => Sync.ImportEnvelopeAsync(EnvelopeFor(Root(index), WallSlot), Token);
        public void Copy(int original, int copy, byte signature, bool forged = false)
        {
            BlsSignature signed = new(Enumerable.Repeat(signature, 96).ToArray());
            blocks.Add(copy, new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = ((ForkedSignedBeaconBlock.OfFulu)this[original]).Block.Message, Signature = signed }));
            if (forged)
                Importer.ForgedSignatures.Add(signed);
        }

        public void AddGloasChild(int parent, int child, ulong slot) => blocks.Add(child, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(slot, Root(parent))));
        public void Serve(params int[] indices) => peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([.. indices.Select(index => this[index])]));
        public void ServeEach(params int[] indices)
        {
            foreach (int index in indices)
            {
                Hash256 root = Root(index);
                peer.RequestBlocksByRootAsync(Arg.Is<Hash256[]>(requested => requested[0] == root), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([this[index]]));
            }
        }

        public void ServeRequestedRoots()
        {
            Dictionary<Hash256, ForkedSignedBeaconBlock> byRoot = blocks.Values.ToDictionary(block => block.ComputeMessageRoot());
            peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([byRoot[((Hash256[])call[0])[0]]]));
        }

        public void Known(params int[] indices)
        {
            foreach (int index in indices)
            {
                Assert.That(Importer.Known, Does.Contain(Root(index)));
            }
        }
        public void Pending(int? retries = null, int? gossip = null)
        {
            if (retries is int retryCount)
                Assert.That(Sync.PendingRetryBlockCount, Is.EqualTo(retryCount));
            if (gossip is int gossipCount)
                Assert.That(Sync.PendingGossipBlockCount, Is.EqualTo(gossipCount));
        }

        public void Imports(int index, int? count = null, bool requested = false, bool byRoot = false)
        {
            IEnumerable<Hash256> imports = byRoot ? Importer.ByRootImports : requested ? Importer.RequestedImports : Importer.Imports.Select(import => import.Root);
            if (count is int expected)
                Assert.That(imports.Count(root => root == Root(index)), Is.EqualTo(expected));
            else
                Assert.That(imports, Does.Contain(Root(index)));
        }

        public void Blame(int count = 1) => peer.Received(count).ReportFailure(PeerFailureReason.ProtocolViolation, Arg.Any<string>());
        public void NoBlame() => peer.DidNotReceive().ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string>());
        public void Seen(int index, bool seen) => Assert.That(harness.Router.IsProposalSeen(this[index].Slot, this[index].ProposerIndex), Is.EqualTo(seen));
        public void Tip(int index) => Assert.That(Sync.SyncTip, Is.EqualTo((Root(index), this[index].Slot)));
        public void Requests(int index, int count) => Assert.That(ByRootRequestsFor(peer, Root(index)), Is.EqualTo(count));
    }
}
