// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.P2P.Gossip;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// The worker gives each gossip message's pending verdict once the checks the router left to it finish: accepted only when every gossip
/// check passed, ignored when a check fails or a dependency is missing, never before the import settles.
/// </summary>
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
    }

    /// <summary>
    /// phase0 p2p-interface.md beacon_block: a block is accepted once its proposer and proposer signature verified, which the importer does
    /// before these results; a block waiting for its columns keeps its verdict until the retry imports it.
    /// </summary>
    [TestCase(GossipBlockOutcome.Imported, new[] { MessageValidity.Accepted })]
    [TestCase(GossipBlockOutcome.ImportedOnceColumnsArrive, new[] { MessageValidity.Accepted })]
    [TestCase(GossipBlockOutcome.ImportedOnceParentPayloadIsVerified, new[] { MessageValidity.Accepted })]
    [TestCase(GossipBlockOutcome.InvalidSignature, new[] { MessageValidity.Ignored })]
    [TestCase(GossipBlockOutcome.UnexpectedProposer, new[] { MessageValidity.Ignored })]
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
        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[0]), CancellationToken.None, verdict);
        MessageValidity[] givenBeforeRetry = [.. given];
        harness.Importer.Unavailable.Remove(blockRoot);
        if (outcome == GossipBlockOutcome.ImportedOnceParentPayloadIsVerified)
        {
            await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(anchorRoot, AnchorSlot), CancellationToken.None);
        }

        await harness.Orchestrator.ProcessSlotAsync(151, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(givenBeforeRetry, outcome is GossipBlockOutcome.ImportedOnceColumnsArrive or GossipBlockOutcome.ImportedOnceParentPayloadIsVerified ? Is.Empty : Is.EqualTo(expected),
                "the verdict waits for the import");
            Assert.That(given, Is.EqualTo(expected));
        }
    }

    /// <summary>A vote or slashing is accepted only when fork choice verified it, signatures included; a refusal is ignored, not charged to its sender.</summary>
    [Test]
    public async Task Gossip_slashing_verdict_follows_fork_choice([Values] bool accepted)
    {
        Harness harness = CreateHarness();
        harness.Importer.AcceptsGossipOperations = accepted;
        harness.Orchestrator.RouteGossipEvents();
        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();

        MessageValidity routed = harness.Router.Handle(GossipTopics.AttesterSlashing, gloasTopic: false,
            GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.FuluSlashing([1, 2], [2, 3], secondSource: 2, secondTarget: 4)), verdict);
        MessageValidity[] givenBeforeWork = [.. given];
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((routed, givenBeforeWork), Is.EqualTo((MessageValidity.Ignored, System.Array.Empty<MessageValidity>())), "the router hands the verdict to the worker");
            Assert.That(given, Is.EqualTo(new[] { accepted ? MessageValidity.Accepted : MessageValidity.Ignored }));
        }
    }

    /// <summary>
    /// gloas/p2p-interface.md execution_payload: an envelope whose payload is recorded passed every gossip check, its signature included;
    /// one waiting for its block keeps its verdict, and any other result is ignored.
    /// </summary>
    [TestCase(ExecutionPayloadEnvelopeImportResult.Valid, new[] { MessageValidity.Accepted })]
    [TestCase(ExecutionPayloadEnvelopeImportResult.Optimistic, new[] { MessageValidity.Accepted })]
    [TestCase(ExecutionPayloadEnvelopeImportResult.Invalid, new[] { MessageValidity.Ignored })]
    [TestCase(ExecutionPayloadEnvelopeImportResult.AlreadyKnown, new[] { MessageValidity.Ignored })]
    [TestCase(ExecutionPayloadEnvelopeImportResult.UnknownBlock, new MessageValidity[0])]
    public async Task Gossip_envelope_verdict_is_given_once_its_payload_import_settles(ExecutionPayloadEnvelopeImportResult result, MessageValidity[] expected)
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeResult = result;
        (GossipVerdict verdict, List<MessageValidity> given) = RecordingVerdict();

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipEnvelopeItem(EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot), verdict));
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        Assert.That(given, Is.EqualTo(expected));
    }

    private static (GossipVerdict Verdict, List<MessageValidity> Given) RecordingVerdict()
    {
        List<MessageValidity> given = [];
        return (new GossipVerdict(validity => { given.Add(validity); return true; }, null), given);
    }
}
