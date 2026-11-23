// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.P2P.Gossip;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;
using Snappier;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    public enum GossipBlockOutcome
    {
        Imported,
        ImportedOnceColumnsArrive,
        ImportedOnceParentPayloadIsVerified,
        InvalidSignature,
        UnexpectedProposer,
        AlreadyKnown,
        UnknownParent,
    }

    [TestCase(GossipBlockOutcome.Imported, new[] { MessageValidity.Accepted })]
    [TestCase(GossipBlockOutcome.ImportedOnceColumnsArrive, new[] { MessageValidity.Ignored })]
    [TestCase(GossipBlockOutcome.UnknownParent, new[] { MessageValidity.Ignored })]
    [TestCase(GossipBlockOutcome.ImportedOnceParentPayloadIsVerified, new[] { MessageValidity.Accepted })]
    [TestCase(GossipBlockOutcome.InvalidSignature, new[] { MessageValidity.Rejected })]
    [TestCase(GossipBlockOutcome.UnexpectedProposer, new[] { MessageValidity.Rejected })]
    [TestCase(GossipBlockOutcome.AlreadyKnown, new[] { MessageValidity.Ignored })]
    public async Task Gossip_block_verdict_is_given_once_its_import_settles(GossipBlockOutcome outcome, MessageValidity[] expected)
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 4);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        Hash256 blockRoot = SszRoots.HashTreeRoot(chain[0].Message!);
        switch (outcome)
        {
            case GossipBlockOutcome.ImportedOnceColumnsArrive:
                harness.Importer.Unavailable.Add(blockRoot);
                break;
            case GossipBlockOutcome.ImportedOnceParentPayloadIsVerified:
                harness.Importer.UnverifiedPayloads.Add(anchorRoot);
                break;
            case GossipBlockOutcome.InvalidSignature:
                harness.Importer.Forged.Add(blockRoot);
                break;
            case GossipBlockOutcome.UnexpectedProposer:
                harness.Importer.ExpectedProposer = false;
                break;
            case GossipBlockOutcome.AlreadyKnown:
                harness.Importer.Known.Add(blockRoot);
                break;
        }

        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();
        SignedBeaconBlock block = outcome == GossipBlockOutcome.UnknownParent ? TestChain.CreateBlock(chain[0].Message!.Slot, TestItem.KeccakF) : chain[0];
        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(block), CancellationToken.None, verdict);
        MessageValidity[] givenBeforeRetry = [.. given];
        harness.Importer.Unavailable.Remove(blockRoot);
        if (outcome == GossipBlockOutcome.ImportedOnceParentPayloadIsVerified)
        {
            await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(anchorRoot, AnchorSlot), CancellationToken.None);
        }

        await harness.Orchestrator.ProcessSlotAsync(151, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(givenBeforeRetry, outcome is GossipBlockOutcome.ImportedOnceParentPayloadIsVerified ? Is.Empty : Is.EqualTo(expected),
            "only a block whose signature verified waits, for its parent's payload");
        Assert.That(given, Is.EqualTo(expected));
    }

    [Test]
    public async Task A_work_item_that_throws_is_skipped_without_charging_its_sender_and_the_worker_goes_on([Values] bool internalTimeout)
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150, 151);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 4);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        Hash256 faultingRoot = SszRoots.HashTreeRoot(chain[0].Message!);
        Hash256 nextRoot = SszRoots.HashTreeRoot(chain[1].Message!);
        harness.Importer.OnImported = (_, root) =>
        {
            if (root == faultingRoot) throw internalTimeout ? new OperationCanceledException("scripted internal timeout") : new InvalidOperationException("scripted fault");
        };
        (GossipVerdict faulting, List<MessageValidity> faultingGiven) = RecordingVerdict();
        (GossipVerdict next, List<MessageValidity> nextGiven) = RecordingVerdict();
        ulong faultsBefore = Metrics.BeaconChainWorkerFaults;
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[0]), faulting));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[1]), next));

        Assert.That(() => CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None), Throws.Nothing);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(faultingGiven, Is.EqualTo(new[] { MessageValidity.Ignored }), "a local fault does not charge the sender, and its id is cached so the same faulting copy is not checked again");
        Assert.That(nextGiven, Is.EqualTo(new[] { MessageValidity.Accepted }), "the worker goes on with the next item");
        Assert.That(harness.Importer.Known, Does.Contain(nextRoot));
        Assert.That(Metrics.BeaconChainWorkerFaults, Is.GreaterThan(faultsBefore));
    }

    [Test]
    public async Task A_held_gossip_block_whose_resumed_import_throws_is_ignored_rather_than_left_pending()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 4);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();
        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[0]), CancellationToken.None, verdict);
        Assert.That(given, Is.Empty, "fixture: the block waits for its parent's payload");
        harness.Importer.UnverifiedPayloads.Remove(anchorRoot);
        harness.Importer.OnImported = (_, _) => throw new InvalidOperationException("scripted fault");

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.FetchedEnvelopeItem(EnvelopeFor(anchorRoot, EnvelopeBlockSlot), null));
        Assert.That(() => CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None), Throws.Nothing);

        Assert.That(given, Is.EqualTo(new[] { MessageValidity.Ignored }), "a local fault does not charge the sender, and its id is cached so the same faulting copy is not checked again");
    }

    [Test]
    public async Task Pending_block_verdict_is_ignored_once_the_block_is_dropped()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 4);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();

        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[0]), CancellationToken.None, verdict);
        MessageValidity[] givenWhileHeld = [.. given];
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: Spec.GetEpoch(chain[0].Message!.Slot) + 1);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(151, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(152, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(givenWhileHeld, Is.Empty, "a block whose signature verified waits for its parent's payload");
        Assert.That(given, Is.EqualTo(new[] { MessageValidity.Ignored }));
    }

    [Test]
    public async Task Gossip_slashing_verdict_follows_fork_choice([Values(true, false, null)] bool? accepted)
    {
        Harness harness = CreateHarness();
        harness.Importer.AcceptsGossipOperations = accepted;
        harness.Orchestrator.RouteGossipEvents();
        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();

        MessageValidity routed = harness.Router.Handle(GossipTopics.AttesterSlashing, gloasTopic: false,
            GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.FuluSlashing([1, 2], [2, 3], secondSource: 2, secondTarget: 4)), verdict);
        MessageValidity[] givenBeforeWork = [.. given];
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((routed, givenBeforeWork), Is.EqualTo((MessageValidity.Ignored, System.Array.Empty<MessageValidity>())), "the router hands the verdict to the worker");
        Assert.That(given, Is.EqualTo(new[] { accepted == true ? MessageValidity.Accepted : accepted == false ? MessageValidity.Rejected : MessageValidity.Ignored }));
    }

    [Test]
    public async Task Gossip_slashing_seen_while_queued_does_not_charge_the_delivering_peer([Values] bool gloas)
    {
        Harness harness = CreateHarness();
        harness.Importer.AcceptsGossipOperations = false;
        harness.Orchestrator.RouteGossipEvents();
        byte[] payload = gloas
            ? GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.GloasSlashing([1, 2], [2, 3], secondSource: 2, secondTarget: 4))
            : GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.FuluSlashing([1, 2], [2, 3], secondSource: 2, secondTarget: 4));
        await GossipRouterTests.AssertDeferredPeerPenaltyAsync(GossipTopics.AttesterSlashing, payload, async verdict =>
        {
            harness.Router.Handle(GossipTopics.AttesterSlashing, gloas, payload, verdict);
            Assert.That(verdict.IsHandedOff, Is.True, "the slashing must reach the queue before another slashing marks its index");
            harness.Router.MarkSlashedIndicesSeen([2]);
            await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        }, MessageValidity.Ignored);
    }

    [TestCase(true, ExecutionPayloadEnvelopeImportResult.DataUnavailable, MessageValidity.Accepted)]
    [TestCase(true, ExecutionPayloadEnvelopeImportResult.EngineUnavailable, MessageValidity.Accepted)]
    [TestCase(false, ExecutionPayloadEnvelopeImportResult.Valid, MessageValidity.Rejected)]
    [TestCase(null, ExecutionPayloadEnvelopeImportResult.UnknownBlock, MessageValidity.Ignored)]
    public async Task Gossip_envelope_verdict_follows_its_signature_before_its_payload_import(bool? signed, ExecutionPayloadEnvelopeImportResult result, MessageValidity expected)
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeSignature = signed;
        harness.Importer.EnvelopeResult = result;
        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipEnvelopeItem(EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot), verdict));
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(given, Is.EqualTo(new[] { expected }));
        Assert.That(harness.Importer.Envelopes, signed == false ? Is.Empty : Has.Count.EqualTo(1), "a badly signed envelope is not imported");
    }

    public enum EnvelopeVerdictState
    {
        Pending,
        AbandonedWhileQueued,
        GivenByRouter,
    }

    [Test]
    public async Task Gossip_envelopes_for_known_blocks_whose_state_is_not_held_trigger_no_regeneration([Values] EnvelopeVerdictState state)
    {
        const int forgeries = 128;
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeSignature = null;
        harness.Importer.EnvelopeResult = ExecutionPayloadEnvelopeImportResult.StateUnavailable;
        List<MessageValidity> given = [];
        for (int i = 0; i < forgeries; i++)
        {
            Hash256 evictedRoot = Keccak.Compute($"evicted {i}");
            harness.Importer.Known.Add(evictedRoot);
            GossipVerdict verdict = state == EnvelopeVerdictState.GivenByRouter ? GossipVerdict.Local() : new(validity => { given.Add(validity); return true; }, null);
            if (state == EnvelopeVerdictState.AbandonedWhileQueued)
            {
                verdict.Abandon();
            }

            Assert.That(harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipEnvelopeItem(EnvelopeFor(evictedRoot, EnvelopeBlockSlot), verdict)), Is.True);
        }

        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Envelopes, Is.Empty, "the import of an evicted block's envelope regenerates its state, which an unverified gossip envelope must not buy, now or from a retry");
        Assert.That(given, state == EnvelopeVerdictState.Pending ? Has.Count.EqualTo(forgeries).And.All.EqualTo(MessageValidity.Ignored) : Is.Empty);
    }

    [Test]
    public async Task Second_signed_gossip_envelope_for_a_block_and_builder_is_ignored()
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeResult = ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        (GossipVerdict first, List<MessageValidity> firstGiven) = RecordingVerdict();
        (GossipVerdict second, List<MessageValidity> secondGiven) = RecordingVerdict();
        SignedExecutionPayloadEnvelope other = EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot);
        other.Message!.ParentBeaconBlockRoot = TestItem.KeccakB;

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipEnvelopeItem(EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot), first));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipEnvelopeItem(other, second));
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        Assert.That((firstGiven.Single(), secondGiven.Single()), Is.EqualTo((MessageValidity.Accepted, MessageValidity.Ignored)));
    }

    [Test]
    public async Task Envelope_settled_at_routing_claims_no_seen_pair()
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeResult = ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot);

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipEnvelopeItem(envelope, GossipVerdict.Local()));
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Router.IsEnvelopeSeen(TestItem.KeccakA, envelope.Message!.BuilderIndex), Is.False);
        Assert.That(harness.Importer.Envelopes, Has.Count.EqualTo(1), "the envelope still imports");
    }

    [Test]
    public async Task Gossip_block_local_failure_does_not_charge_the_delivering_peer([Values] bool cancelled)
    {
        Harness harness = CreateHarness();
        SignedBeaconBlock block = TestChain.CreateBlock(WallSlot, TestItem.KeccakA);
        IBlockImporter importer = Substitute.For<IBlockImporter>();
        importer.IsKnown(TestItem.KeccakA).Returns(true);
        using CancellationTokenSource shutdown = new();
        Exception failure = cancelled ? new OperationCanceledException(shutdown.Token) : new InvalidOperationException("store unavailable");
        importer.Import(Arg.Any<ForkedSignedBeaconBlock>(), Arg.Any<Hash256>(), true).Returns(_ =>
        {
            if (cancelled) shutdown.Cancel();
            throw failure;
        });
        harness.Orchestrator.Initialize(importer, new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(0, Hash256.Zero)), Hash256.Zero);
        await GossipRouterTests.AssertDeferredPeerPenaltyAsync(GossipTopics.BeaconBlock,
            Snappier.Snappy.CompressToArray(SignedBeaconBlock.Encode(block)), async verdict =>
            {
                harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(new ForkedSignedBeaconBlock.OfFulu(block), verdict));
                if (cancelled)
                    Assert.That(await Assert.CatchAsync(async () => await harness.Orchestrator.ProcessQueuedAsync(shutdown.Token)),
                        Is.SameAs(failure), "a cancellation stops the worker after settling the relay verdict");
                else
                    Assert.That(async () => await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None), Throws.Nothing, "a local fault skips the item, not the worker");
            }, MessageValidity.Ignored);
    }

    internal static async Task AssertBlockVerdictAsync(IBlockImporter importer, ForkedSignedBeaconBlock block, byte[] payload, MessageValidity expected, GossipRouter? router = null)
    {
        Harness harness = CreateHarness(anchorSlot: 0, wallSlot: block.Slot);
        SignedBeaconBlock anchor = TestChain.CreateBlock(0, Hash256.Zero);
        harness.Orchestrator.Initialize(importer, new ForkedSignedBeaconBlock.OfFulu(anchor), SszRoots.HashTreeRoot(anchor.Message!));
        await GossipRouterTests.AssertDeferredPeerPenaltyAsync(GossipTopics.BeaconBlock, payload, async verdict =>
        {
            if (router is null)
            {
                await harness.Orchestrator.ProcessGossipBlockAsync(block, CancellationToken.None, verdict);
                return;
            }

            (ForkedSignedBeaconBlock Block, GossipVerdict Verdict)? received = null;
            router.BeaconBlockReceived += (decoded, pending) => received = (decoded, pending);
            MessageValidity immediate = router.Handle(GossipTopics.BeaconBlock, block is ForkedSignedBeaconBlock.OfGloas, payload, verdict);
            if (received is { } item)
                await harness.Orchestrator.ProcessGossipBlockAsync(item.Block, CancellationToken.None, item.Verdict);
            else
                verdict.Complete(immediate);
        }, expected);
    }

    internal static async Task AssertOperationVerdictAsync(IBlockImporter importer, ulong slot, string topic, byte[] payload,
        Func<GossipVerdict, BeaconSyncOrchestrator.WorkItem> work, MessageValidity expected, GossipRouter? router = null, bool gloas = false, bool computeHead = true,
        BeaconSyncOrchestrator.WorkItem? preceding = null)
    {
        Harness harness = CreateHarness(anchorSlot: 0, wallSlot: slot);
        SignedBeaconBlock anchor = TestChain.CreateBlock(0, Hash256.Zero);
        harness.Orchestrator.Initialize(importer, new ForkedSignedBeaconBlock.OfFulu(anchor), SszRoots.HashTreeRoot(anchor.Message!));
        if (computeHead) importer.ComputeHead();
        if (router is not null)
        {
            router.AggregateAndProofReceived += (aggregate, verdict) => harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipAggregateItem(aggregate, verdict));
            router.GloasAggregateAndProofReceived += (aggregate, verdict) => harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipGloasAggregateItem(aggregate, verdict));
        }
        await GossipRouterTests.AssertDeferredPeerPenaltyAsync(topic, payload, async verdict =>
        {
            if (preceding is not null) harness.Orchestrator.WorkWriter.TryWrite(preceding);
            if (router is null)
                harness.Orchestrator.WorkWriter.TryWrite(work(verdict));
            else
            {
                MessageValidity immediate = router.Handle(topic, gloas, payload, verdict);
                if (!verdict.IsHandedOff) verdict.Complete(immediate);
            }
            await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        }, expected);
    }

    internal static async Task AssertOperationAfterSkippedTickAsync(BlockImporter importer, ulong slot, GossipRouter router, string topic, byte[] payload, bool gloas)
    {
        Harness harness = CreateHarness(anchorSlot: 0, wallSlot: slot - 1, router: router);
        SignedBeaconBlock anchor = TestChain.CreateBlock(0, Hash256.Zero);
        harness.Orchestrator.Initialize(importer, new ForkedSignedBeaconBlock.OfFulu(anchor), SszRoots.HashTreeRoot(anchor.Message!));
        importer.ComputeHead();
        harness.Orchestrator.RouteGossipEvents();
        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();

        await harness.Orchestrator.EnqueueSlotTickAsync(slot, CancellationToken.None);
        router.Handle(topic, gloas, payload, verdict);
        Assert.That(verdict.IsHandedOff, Is.True, "the router must queue the operation for the real importer");
        await harness.Orchestrator.EnqueueSlotTickAsync(slot + 1, CancellationToken.None);
        Assert.That(given, Is.Empty, "both ticks must be queued before the worker validates gossip");

        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        Assert.That(given, Is.EqualTo(new[] { MessageValidity.Accepted }),
            "applying the skipped tick must restore the cached head before gossip uses the available validation state");
    }

    [Test]
    public async Task Worker_accepts_gossip_queued_during_import_before_next_tick([Values] bool invalidImport, [Values] bool slashing)
    {
        const ulong slot = 33;
        SignedGloasChain chain = new();
        BeaconChainStore store = chain.CreateStore();
        SlotClock clock = new(chain.Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime + slot * chain.Spec.SecondsPerSlot)));
        IEngineDriver engine = Substitute.For<IEngineDriver>();
        engine.NotifyNewPayload(Arg.Any<BeaconBlockBody>(), out Arg.Any<Hash256?>()).Returns(ExecutionStatus.Invalid);
        BlockImporter importer = chain.CreateImporter(engine, store: store, clock: clock);
        SignedGloasChain.Block first = chain.Next(null, slot - 1, full: false, 0xA1);
        ForkedSignedBeaconBlock child = invalidImport ? chain.NextFulu(slot - 2).Forked : chain.Next(first, slot, full: false, 0xA2).Forked;
        Assert.That(importer.Import(first.Forked, first.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        string topic = slashing ? GossipTopics.AttesterSlashing : GossipTopics.BeaconAggregateAndProof;
        byte[] payload = Snappy.CompressToArray(slashing
            ? AttesterSlashing.Encode(GloasBlockImporterTests.SignedSlashing(chain, first))
            : SignedAggregateAndProofGloas.Encode(GloasBlockImporterTests.SignedAggregate(first)));
        GossipRouter router = new(chain.Spec, clock, LimboLogs.Instance, store);
        Harness harness = CreateHarness(anchorSlot: 0, wallSlot: slot, router: router);
        GossipDuringImport forwarding = new(importer);
        harness.Orchestrator.Initialize(forwarding, new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);
        harness.Orchestrator.RouteGossipEvents();
        await harness.Orchestrator.EnqueueSlotTickAsync(slot, CancellationToken.None);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();
        forwarding.AfterImport = result =>
        {
            Assert.That(result, Is.EqualTo(invalidImport ? BlockImportResult.Invalid : BlockImportResult.Imported), "the real importer must reach the requested execution outcome");
            Assert.That(((IBlockImporter)importer).IsHeadStale, Is.True, "both successful and INVALID imports must invalidate the cached head");
            router.Handle(topic, gloasTopic: !slashing, payload, verdict);
            Assert.That(verdict.IsHandedOff, Is.True, "gossip must reach the worker before the next tick");
            Assert.That(harness.Orchestrator.EnqueueSlotTickAsync(slot + 1, CancellationToken.None).IsCompletedSuccessfully, Is.True);
            Assert.That(given, Is.Empty, "the vote must wait until the import returns to the worker");
        };
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(child));

        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(importer.IsKnown(child.ComputeMessageRoot()), Is.EqualTo(!invalidImport), "an invalid payload must not enter fork choice");
        Assert.That(forwarding.VoteSlots, Is.EqualTo(new[] { slot }), "the vote must be consumed before the next queued tick");
        Assert.That(given, Is.EqualTo(new[] { MessageValidity.Accepted }), "head invalidation must not hide available gossip validation state, even when the import fails");
    }

    private sealed class GossipDuringImport(IBlockImporter inner) : IBlockImporter
    {
        public Action<BlockImportResult>? AfterImport { get; set; }
        public List<ulong> VoteSlots { get; } = [];
        private ulong _appliedSlot;

        bool IBlockImporter.IsHeadStale => inner.IsHeadStale;
        public bool IsKnown(Hash256 root) => inner.IsKnown(root);
        public HeadView ComputeHead() => inner.ComputeHead();
        public void OnSlotTick(ulong slot) => inner.OnSlotTick(_appliedSlot = slot);
        public void OnForkchoiceUpdated(Hash256 root, Hash256 hash, PayloadStatusV1 status) => inner.OnForkchoiceUpdated(root, hash, status);
        public void OnFinalized(CheckpointRef finalized) => inner.OnFinalized(finalized);
        public BlockImportResult Import(ForkedSignedBeaconBlock block, Hash256 root, bool verifySignatures) => inner.Import(block, root, verifySignatures);
        public ExecutionPayloadEnvelopeImportResult ImportEnvelope(SignedExecutionPayloadEnvelope envelope) => inner.ImportEnvelope(envelope);
        public bool? VerifyEnvelopeSignature(SignedExecutionPayloadEnvelope envelope) => inner.VerifyEnvelopeSignature(envelope);
        public bool? OnGossipAggregate(SignedAggregateAndProof aggregate) => RecordVote(() => inner.OnGossipAggregate(aggregate));
        public bool? OnGossipAggregate(SignedAggregateAndProofGloas aggregate) => RecordVote(() => inner.OnGossipAggregate(aggregate));
        public bool? OnGossipAttesterSlashing(AttesterSlashing slashing) => RecordVote(() => inner.OnGossipAttesterSlashing(slashing));
        public bool? OnGossipAttesterSlashing(AttesterSlashingGloas slashing) => RecordVote(() => inner.OnGossipAttesterSlashing(slashing));
        public bool? OnGossipPayloadAttestation(PayloadAttestationMessage message) => RecordVote(() => inner.OnGossipPayloadAttestation(message));

        public BlockImportResult ImportRequested(ForkedSignedBeaconBlock block, Hash256 root, bool fetchedByRoot = false)
        {
            BlockImportResult result = inner.ImportRequested(block, root, fetchedByRoot);
            AfterImport?.Invoke(result);
            return result;
        }

        private bool? RecordVote(Func<bool?> validate)
        {
            VoteSlots.Add(_appliedSlot);
            return validate();
        }
    }

    private static (GossipVerdict Verdict, List<MessageValidity> Given) RecordingVerdict()
    {
        List<MessageValidity> given = [];
        return (new GossipVerdict(validity => { given.Add(validity); return true; }, null), given);
    }
}
