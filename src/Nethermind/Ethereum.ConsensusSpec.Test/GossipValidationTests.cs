// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <remarks>
/// Raised messages passed synchronous checks, not later import signature/state validation.
/// Valid must be raised, ignore must not reject, and reject must reject; unsupported rejects are not-implemented.
/// Verdict rows are checked both ways. Minimal containers are unsupported by mainnet SSZ bounds.
/// </remarks>
[TestFixture]
public class GossipValidationTests
{
    private const string Suite = "networking";

    private const string NotImplementedSentinel = "";

    internal static readonly (string Fork, string[] Handlers)[] Suites =
    [
        ("fulu", ["gossip_attester_slashing", "gossip_beacon_aggregate_and_proof", "gossip_beacon_block"]),
        ("gloas", ["gossip_attester_slashing", "gossip_beacon_aggregate_and_proof", "gossip_beacon_block", "gossip_execution_payload_envelope", "gossip_payload_attestation_message"]),
    ];

    private static readonly string[] UnroutedHandlers =
    [
        "gossip_beacon_attestation", "gossip_bls_to_execution_change", "gossip_partial_data_column_sidecar", "gossip_proposer_slashing",
        "gossip_sync_committee_contribution_and_proof", "gossip_sync_committee_message", "gossip_voluntary_exit",
    ];

    /// <summary>Enumerates unsubscribed topics as not-implemented; column vectors run in <see cref="DataColumnSidecarNetworkingTests"/>.</summary>
    internal static readonly (string Fork, string[] Handlers)[] UnroutedSuites =
    [
        ("fulu", [.. UnroutedHandlers]),
        ("gloas", [.. UnroutedHandlers, "gossip_execution_payload_bid", "gossip_proposer_preferences"]),
    ];

    /// <summary>Pins synchronous verdicts by topic/reason; every observed verdict needs a row and every row must be exercised.</summary>
    internal static readonly IReadOnlyDictionary<string, (string Reason, RouterVerdict Verdict)[]> SynchronousVerdicts =
        new Dictionary<string, (string, RouterVerdict)[]>(StringComparer.Ordinal)
        {
            [GossipTopics.BeaconBlock] =
            [
                // phase0/p2p-interface.md: a future-slot message MAY be queued; the router holds an early next-slot block until its slot.
                ("block is from a future slot", RouterVerdict.Deferred),
                ("block is not from a slot greater than the latest finalized slot", RouterVerdict.Ignored(GossipDropReason.BeforeFinalized)),
                ("block is not the first valid block for this slot and proposer", RouterVerdict.Ignored(GossipDropReason.Duplicate)),
                ("block's parent has not been seen", RouterVerdict.Ignored(GossipDropReason.InvalidField)),
                // gloas/p2p-interface.md: verify_block_body_operation_limits and verify_execution_requests_limits on the block.
                ("block must not contain deposits", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many attestations", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many attester slashings", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many bls to execution changes", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many payload attestations", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many proposer slashings", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many voluntary exits", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many builder deposit requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many builder exit requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many consolidation requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many withdrawal requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                // Field REJECTs follow parent prerequisites proven by the published snapshot.
                ("incorrect execution payload timestamp", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("too many blob kzg commitments", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("bid's parent does not equal block's parent", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("block is not from a higher slot than its parent", RouterVerdict.Rejected(GossipDropReason.NotAboveParentSlot)),
            ],
            [GossipTopics.BeaconAggregateAndProof] =
            [
                ("aggregate data index is non-zero", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("aggregate data index must be 0 or 1", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("aggregate committee bits must specify exactly one committee", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                // The only future-slot aggregate opens the next epoch, so the epoch-window IGNORE drops it before it could be held.
                ("aggregate slot is from a future slot", RouterVerdict.Ignored(GossipDropReason.StaleSlot)),
                ("aggregate epoch is not current or previous epoch", RouterVerdict.Ignored(GossipDropReason.StaleSlot)),
                ("already seen aggregate for this data", RouterVerdict.Ignored(GossipDropReason.Duplicate)),
                ("already seen aggregate for this epoch and aggregator", RouterVerdict.Ignored(GossipDropReason.Duplicate)),
                ("block being voted for has not been seen", RouterVerdict.Ignored(GossipDropReason.UnknownBlock)),
                ("block being voted for failed validation", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                // REJECTs needing no state that the spec orders after store checks; gossip_validation.md lets them run in any order.
                ("attestation epoch does not match target epoch", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("aggregate has no participants", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                // gloas/p2p-interface.md verify_attestation_payload_status follows the finalized-ancestry IGNORE.
                ("same-slot attestation must attest with index 0", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
            ],
            [GossipTopics.AttesterSlashing] =
            [
                ("all attester slashing indices already seen", RouterVerdict.Ignored(GossipDropReason.Duplicate)),
                ("all attester slashing indices already seen", RouterVerdict.Ignored(GossipDropReason.InvalidField)),
                ("attestation data is not slashable", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                // is_valid_indexed_attestation's sorted and unique indices need no state; its signature does.
                ("invalid indexed attestation 1", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("invalid indexed attestation 2", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
            ],
            // An envelope whose block is not held is raised, so "envelope's block has not been seen" has no row; neither has the
            // signature, which needs the state.
            [GossipTopics.ExecutionPayload] =
            [
                ("envelope's block failed validation", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                // gloas/p2p-interface.md: verify_execution_requests_limits on the envelope.
                ("too many builder deposit requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many builder exit requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many consolidation requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many withdrawal requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("envelope is from a slot before the latest finalized slot", RouterVerdict.Ignored(GossipDropReason.BeforeFinalized)),
                ("already seen envelope for this block root from this builder", RouterVerdict.Ignored(GossipDropReason.Duplicate)),
                ("block's slot does not match payload's slot number", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("envelope's builder index does not match the bid's builder index", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("payload's block hash does not match the bid's block hash", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("envelope's execution requests root does not match the bid's", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("too many withdrawals", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
            ],
            // PTC membership and the signature need the state, so those votes are raised for fork choice to verify.
            [GossipTopics.PayloadAttestationMessage] =
            [
                ("payload attestation's slot is pre-gloas", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("already seen payload attestation from this validator", RouterVerdict.Ignored(GossipDropReason.Duplicate)),
                ("payload attestation is not for the current slot", RouterVerdict.Ignored(GossipDropReason.StaleSlot)),
                ("payload attestation is not for the current slot", RouterVerdict.Ignored(GossipDropReason.FutureSlot)),
                ("payload attestation's block has not been seen", RouterVerdict.Ignored(GossipDropReason.UnknownBlock)),
                ("payload attestation's block failed validation", RouterVerdict.Rejected(GossipDropReason.InvalidField)),
                ("payload attestation's block is not at the assigned slot", RouterVerdict.Ignored(GossipDropReason.InvalidField)),
            ],
        };

    private static readonly GossipDropReason[] DropReasons = Enum.GetValues<GossipDropReason>();

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(GossipValidationCase testCase) => Execute(testCase);
    [Test]
    public void Every_fork_and_handler_has_vectors_in_the_archive()
    {
        IEnumerable<string> enumerated = TestedCases().Select(KeyOf).Distinct();
        IEnumerable<string> expected = Suites.SelectMany(static s => s.Handlers.Select(handler => $"{s.Fork}/{handler}"));
        Assert.That(enumerated, Is.EquivalentTo(expected));
    }
    [Test]
    public void Every_fork_and_handler_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented()
    {
        List<GossipValidationCase> cases = TestedCases();
        IEnumerable<GossipValidationCase> sentinels = cases.DistinctBy(KeyOf).Select(static c => c with { CasePath = NotImplementedSentinel });
        FuluDriverSupport.AssertEveryKeyRunsSomeVector([.. sentinels, .. cases], KeyOf, static testCase =>
        {
            if (testCase.CasePath == NotImplementedSentinel)
                throw new NotImplementedInDriverException("sentinel");
            Run(testCase);
        });
    }
    [Test]
    public void Every_verdict_row_is_reached_and_every_unchecked_reject_is_reported_not_implemented()
    {
        HashSet<(string Topic, string Reason, RouterVerdict Verdict)> reached = [];
        List<string> misreported = [];
        foreach (GossipValidationCase testCase in TestedCases())
        {
            List<Observation> observations = [];
            bool reported = false;
            try
            {
                Run(testCase, observations);
            }
            catch (NotImplementedInDriverException)
            {
                reported = true;
            }

            foreach (Observation observed in observations)
            {
                if (observed.Reason is not null && observed.Verdict != RouterVerdict.Raised)
                    reached.Add((observed.Topic, observed.Reason, observed.Verdict));
            }

            bool hasUncheckedReject = observations.Any(static o => o.Expected == "reject" && o.Verdict.Action != RouterAction.Rejected);
            if (reported != hasUncheckedReject)
                misreported.Add($"{testCase.VectorName} {(hasUncheckedReject ? "passed but has a reject the router does not reject" : "reported not-implemented")}");
        }

        IEnumerable<(string, string, RouterVerdict)> rows = SynchronousVerdicts.SelectMany(static entry => entry.Value.Select(row => (entry.Key, row.Reason, row.Verdict)));
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(misreported, Is.Empty);
        Assert.That(reached, Is.EquivalentTo(rows));
    }
    [Test]
    public void Every_unrouted_handler_has_vectors_and_its_topic_is_not_routed()
    {
        if (FuluDriverSupport.CompiledPreset == ConsensusPreset.Mainnet && !ConsensusSpecArchive.MainnetEnabled)
            Assert.Ignore("mainnet vectors are opt-in (NETHERMIND_CONSENSUS_SPEC_MAINNET=1)");

        // A gossip handler the archive gains must be driven, listed as unrouted or run by the column suite, never left out unseen.
        foreach ((string fork, string[] driven) in Suites)
        {
            string[] archived = [.. ConsensusSpecArchive.SubDirs(ConsensusSpecArchive.SuitePath(FuluDriverSupport.CompiledPreset, fork, Suite)).Select(Path.GetFileName).Where(static name => name!.StartsWith("gossip_", StringComparison.Ordinal))!];
            string[] unroutedInFork = UnroutedSuites.Single(suite => suite.Fork == fork).Handlers;
            Assert.That(archived, Is.EquivalentTo([.. driven, .. unroutedInFork, "gossip_data_column_sidecar"]), $"{fork} gossip handlers in the archive");
        }

        List<GossipValidationCase> unrouted = [.. AllCases().Where(IsUnrouted)];
        IEnumerable<string> expected = UnroutedSuites.SelectMany(static s => s.Handlers.Select(handler => $"{s.Fork}/{handler}"));
        GossipRouter probe = new(FuluDriverSupport.DefaultSpec, new SlotClock(FuluDriverSupport.DefaultSpec, new ManualTimestamper(DateTime.UnixEpoch)), LimboLogs.Instance);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(unrouted.Select(KeyOf).Distinct(), Is.EquivalentTo(expected));
        // The router's own dispatch is the probe: a subnet topic name carries a numeric suffix, so both forms are tried.
        Assert.That(UnroutedSuites.SelectMany(static s => s.Handlers).Select(TopicOf).Distinct().SelectMany(static topic => new[] { topic, $"{topic}_0" }).Where(name => Routes(probe, name)), Is.Empty);
        Assert.That(Suites.SelectMany(static s => s.Handlers).Select(TopicOf).Distinct().Where(name => !Routes(probe, name)), Is.Empty, "driven handlers must reach a router arm");
    }

    // gossip_validation.md: offset_ms counts from the vector's current_time_ms; a message's own current_time_ms, which the vectors also use, is absolute.
    [TestCase("current_time_ms: 12000\nmessages:\n- {offset_ms: 500, message: m, expected: valid}", 12500)]
    [TestCase("current_time_ms: 12000\nmessages:\n- {current_time_ms: 12100, message: m, expected: valid}", 12100)]
    [TestCase("messages:\n- {message: m, expected: valid}", 0)]
    public void Message_time_is_its_own_or_the_vector_time_plus_its_offset(string yaml, long expectedTimeMs) =>
        Assert.That(VectorMeta.Parse(new StringReader($"topic: beacon_block\n{yaml}")).Messages.Single().TimeMs, Is.EqualTo(expectedTimeMs));

    [Test]
    public void Finalized_checkpoint_override_is_read() =>
        Assert.That(VectorMeta.Parse(new StringReader("topic: beacon_block\nfinalized_checkpoint: {epoch: 7, root: '0x00'}\nmessages:\n- {message: m, expected: valid}")).FinalizedEpoch, Is.EqualTo(7UL));

    [Test]
    public void Vector_without_messages_is_malformed() =>
        Assert.That(() => VectorMeta.Parse(new StringReader("topic: beacon_block\nmessages: []")), Throws.InstanceOf<InvalidDataException>());

    private static bool Routes(GossipRouter router, string topicName)
    {
        try
        {
            router.HandlerFor(topicName);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // The handler for the Gloas envelope topic is named after the envelope, not the topic.
    private static string TopicOf(string handler) =>
        handler == "gossip_execution_payload_envelope" ? GossipTopics.ExecutionPayload : handler["gossip_".Length..];

    private static bool IsUnrouted(GossipValidationCase testCase) =>
        UnroutedSuites.Any(suite => suite.Fork == testCase.Fork && suite.Handlers.Contains(testCase.Handler));

    private static string KeyOf(GossipValidationCase testCase) => $"{testCase.Fork}/{testCase.Handler}";
    private static List<GossipValidationCase> TestedCases() => [.. AllCases().Where(static testCase => !IsUnrouted(testCase))];

    private static List<GossipValidationCase> AllCases() =>
        FuluDriverSupport.TestedCases<GossipValidationCase>(FuluDriverSupport.CompiledPreset, MainnetCases, MainnetCases);

    private static void Execute(GossipValidationCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord(Suite, testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(GossipValidationCase testCase, ICollection<Observation>? observations = null)
    {
        if (IsUnrouted(testCase))
            throw new NotImplementedInDriverException($"GossipRouter neither subscribes nor validates the {TopicOf(testCase.Handler)} topic, so its rules have no code to run against");

        VectorMeta meta = VectorMeta.Load(testCase.CasePath);
        bool gloas = testCase.Fork == "gloas";
        (ulong genesisTime, ulong anchorFinalizedEpoch) = ReadAnchorState(Path.Combine(testCase.CasePath, "state.ssz_snappy"), gloas);
        BeaconChainSpec spec = WithGenesisTime(VectorSpec(testCase.CasePath, gloas), genesisTime);
        long slotMs = (long)spec.SecondsPerSlot * 1000;
        DateTime genesis = DateTimeOffset.FromUnixTimeSeconds((long)spec.GenesisTime).UtcDateTime;
        ManualTimestamper timestamper = new(genesis);
        BeaconChainStatusHolder status = new(spec, timestamper)
        {
            CurrentStatus = new StatusMessageV2
            {
                FinalizedEpoch = meta.FinalizedEpoch ?? anchorFinalizedEpoch,
                FinalizedRoot = Hash256.Zero,
                HeadRoot = Hash256.Zero,
            },
        };
        SeededBlocks seeded = SeedBlocks(testCase.CasePath, meta, spec, gloas);
        SlotClock clock = new(spec, timestamper);
        ColumnGossipRouter headers = new(spec, clock, LimboLogs.Instance, store: seeded.Store, status: status,
            forkChoice: SeedForkChoice(testCase.CasePath, seeded, status.CurrentStatus.FinalizedEpoch));
        GossipRouter router = new(spec, clock, LimboLogs.Instance, seeded.Store, status,
            seeded.FailedBlocks, headers);
        int raised = 0;
        Action? markVerified = null;
        router.BeaconBlockReceived += (_, _) => raised++;
        router.AggregateAndProofReceived += (a, _) =>
        {
            raised++;
            markVerified = () => router.MarkAggregateSeen(a.Message!.Aggregate!.Data!, a.Message.Aggregate.CommitteeBits!, a.Message.Aggregate.AggregationBits!, a.Message.AggregatorIndex);
        };
        router.GloasAggregateAndProofReceived += (a, _) =>
        {
            raised++;
            markVerified = () => router.MarkAggregateSeen(a.Message!.Aggregate!.Data!, a.Message.Aggregate.CommitteeBits!, a.Message.Aggregate.AggregationBits!, a.Message.AggregatorIndex);
        };
        router.AttesterSlashingReceived += (_, _) => raised++;
        router.GloasAttesterSlashingReceived += (_, _) => raised++;
        router.ExecutionPayloadEnvelopeReceived += (_, _) => raised++;
        router.PayloadAttestationMessageReceived += (_, _) => raised++;

        List<string> failures = [];
        List<string> uncheckedRejects = [];
        for (int i = 0; i < meta.Messages.Count; i++)
        {
            VectorMessage message = meta.Messages[i];
            string label = $"message {i} ({message.Name}) expected {message.Expected}{(message.Reason is null ? "" : $" ({message.Reason})")}";
            long[] dropsBefore = [.. DropReasons.Select(router.GetDropCount)];
            int raisedBefore = raised;
            markVerified = null;

            timestamper.UtcNow = genesis.AddMilliseconds(message.TimeMs);
            MessageValidity validity = router.Handle(meta.Topic, gloas, File.ReadAllBytes(Path.Combine(testCase.CasePath, message.Name + ".ssz_snappy")));
            GossipDropReason[] drops = [.. DropReasons.Where((reason, index) => router.GetDropCount(reason) != dropsBefore[index])];

            RouterVerdict? verdict = (drops, raised - raisedBefore, validity) switch
            {
                ([GossipDropReason.Oversized or GossipDropReason.InvalidSnappy or GossipDropReason.InvalidSsz], _, _) => null,
                ([GossipDropReason drop], 0, MessageValidity.Rejected) => RouterVerdict.Rejected(drop),
                ([GossipDropReason drop], 0, MessageValidity.Ignored) => RouterVerdict.Ignored(drop),
                ([], 1, MessageValidity.Ignored) => RouterVerdict.Raised,
                ([], 0, MessageValidity.Ignored) => ReleasedAtNextSlot(message.TimeMs) ? RouterVerdict.Deferred : null,
                _ => null,
            };

            if (verdict is not { } actual)
            {
                failures.Add($"{label} but the router returned {validity} with drops [{string.Join(", ", drops)}] and {raised - raisedBefore} events: undecoded, or held past the next slot");
                continue;
            }

            observations?.Add(new Observation(meta.Topic, message.Expected, message.Reason, actual));
            // The import pipeline marks the aggregate seen sets once a raised aggregate verifies, as one the vector expects valid would.
            if (actual == RouterVerdict.Raised && message.Expected == "valid")
                markVerified?.Invoke();

            if (actual != RouterVerdict.Raised && (message.Reason is null || !IsSynchronousVerdict(meta.Topic, message.Reason, actual)))
                failures.Add($"{label} but the router's verdict {actual} has no row in {nameof(SynchronousVerdicts)}");

            switch (message.Expected)
            {
                case "valid" when actual != RouterVerdict.Raised:
                case "ignore" when actual.Action == RouterAction.Rejected:
                    failures.Add($"{label} but was {actual}");
                    break;
                case "valid" or "ignore":
                case "reject" when actual.Action == RouterAction.Rejected:
                    break;
                case "reject" when actual == RouterVerdict.Raised:
                    uncheckedRejects.Add($"{message.Reason ?? $"message {i}"} (raised: the rule needs BLS or beacon state, or follows a check that does)");
                    break;
                case "reject":
                    uncheckedRejects.Add($"{message.Reason ?? $"message {i}"} (the router's verdict is {actual})");
                    break;
                default:
                    throw new InvalidDataException($"{label}: unknown expected result");
            }
        }

        if (failures.Count > 0)
            Assert.Fail(string.Join("; ", failures));

        if (uncheckedRejects.Count > 0)
            throw new NotImplementedInDriverException($"GossipRouter does not reject: {string.Join("; ", uncheckedRejects)}");

        // A held message must be raised once the next slot starts, which tells a deferral from a drop that counted nothing.
        bool ReleasedAtNextSlot(long timeMs)
        {
            int before = raised;
            timestamper.UtcNow = genesis.AddMilliseconds((timeMs / slotMs + 1) * slotMs);
            router.ReleaseDueMessages();
            return raised == before + 1;
        }
    }

    internal sealed record SeededBlocks(BeaconChainStore Store, FailedBlockRoots FailedBlocks, List<(VectorBlock Entry, ForkedSignedBeaconBlock Block)> Blocks);

    internal static SeededBlocks SeedBlocks(string casePath, VectorMeta meta, BeaconChainSpec spec, bool gloas = false)
    {
        SeededBlocks seeded = new(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>(), spec), new FailedBlockRoots(), []);
        foreach (VectorBlock entry in meta.Blocks)
        {
            byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, entry.Name + ".ssz_snappy"));
            // A node never holds a block whose shape is not its slot's fork.
            if (gloas && SignedBeaconBlockCodec.TryReadSlot(ssz, out ulong slot) && !SignedBeaconBlockCodec.IsGloasSlot(slot, spec))
                continue;

            ForkedSignedBeaconBlock block = SignedBeaconBlockCodec.Decode(ssz, spec);
            Hash256 root = block.ComputeMessageRoot();
            if (entry.Failed)
                seeded.FailedBlocks.Add(root, block.Slot);
            else
            {
                seeded.Store.PutForkedBlock(root, block);
                seeded.Blocks.Add((entry, block));
            }
        }

        return seeded;
    }

    private static bool IsSynchronousVerdict(string topic, string reason, RouterVerdict verdict) =>
        SynchronousVerdicts.TryGetValue(topic, out (string Reason, RouterVerdict Verdict)[]? rows) && rows.Contains((reason, verdict));

    private static ForkChoiceSnapshotHolder SeedForkChoice(string casePath, SeededBlocks seeded, ulong finalizedEpoch)
    {
        List<ForkChoiceSnapshotNode> nodes = [];
        foreach ((VectorBlock entry, ForkedSignedBeaconBlock block) in seeded.Blocks)
        {
            if (entry.Pending)
                continue;

            ExecutionStatus execution = entry.PayloadStatus switch
            {
                "INVALIDATED" => ExecutionStatus.Invalid,
                "VALID" => ExecutionStatus.Valid,
                _ => ExecutionStatus.Optimistic,
            };
            Hash256? executionHash = block switch
            {
                ForkedSignedBeaconBlock.OfGloas signed => signed.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlockHash,
                ForkedSignedBeaconBlock.OfFulu fulu => fulu.Block.Message!.Body!.ExecutionPayload!.BlockHash,
                _ => null,
            };
            nodes.Add(new(block.Slot, block.ComputeMessageRoot(), block.ParentRoot, 0, 0, 0, execution, executionHash,
                PayloadValid: entry.PayloadStatus == "VALID" && (block is ForkedSignedBeaconBlock.OfFulu || entry.PayloadPresent)));
        }

        nodes.Sort(static (left, right) => left.Slot.CompareTo(right.Slot));
        CheckpointRef finalized = new(finalizedEpoch, DataColumnSidecarNetworkingTests.ReadFinalizedRoot(casePath) ?? nodes.FirstOrDefault()?.Root ?? Hash256.Zero);
        return new() { Current = new(finalized, finalized, Hash256.Zero, nodes) };
    }

    /// <exception cref="NotImplementedInDriverException">The config's slot duration differs from the spec's, which <see cref="SlotClock"/> would misread.</exception>
    internal static BeaconChainSpec VectorSpec(string casePath, bool gloas)
    {
        string configPath = Path.Combine(casePath, "config.yaml");
        if (!File.Exists(configPath))
            return FuluDriverSupport.TransitionSpec(gloas ? 0 : Presets.FarFutureEpoch);

        BeaconChainSpec spec = FuluDriverSupport.CaseSpec(casePath);
        if (FuluDriverSupport.ParseFlowMap(configPath).TryGetValue("SLOT_DURATION_MS", out string? slotDuration)
            && ulong.Parse(slotDuration) != spec.SecondsPerSlot * 1000)
        {
            throw new NotImplementedInDriverException($"config.yaml SLOT_DURATION_MS {slotDuration} differs from the spec's {spec.SecondsPerSlot * 1000}");
        }

        return spec;
    }

    internal static (ulong GenesisTime, ulong FinalizedEpoch) ReadAnchorState(string statePath, bool gloas)
    {
        if (!gloas)
        {
            BeaconStateFulu fulu = FuluDriverSupport.DecodeState(statePath);
            return (fulu.GenesisTime, fulu.FinalizedCheckpoint!.Epoch);
        }

        BeaconStateGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(statePath), out BeaconStateGloas state);
        return (state.GenesisTime, state.FinalizedCheckpoint!.Epoch);
    }

    // compute_time_at_slot reads the anchor state's genesis_time, which the vectors set apart from the network config's.
    internal static BeaconChainSpec WithGenesisTime(BeaconChainSpec spec, ulong genesisTime) => spec with
    {
        GenesisTime = genesisTime,
    };

    private static IEnumerable<TestCaseData> MainnetCases()
    {
        if (FuluDriverSupport.CompiledPreset == ConsensusPreset.Mainnet && !ConsensusSpecArchive.MainnetEnabled) yield break;

        foreach ((string fork, string[] handlers) in Suites.Concat(UnroutedSuites))
        {
            foreach (TestCaseData testCase in FuluDriverSupport.HandlerCases(FuluDriverSupport.CompiledPreset, [fork], Suite, "meta.yaml",
                         static (p, f, handler, path, name) => new GossipValidationCase(p.ToString(), f, handler, path, name), handlers, relativeNames: true))
                yield return testCase;
        }
    }

    private sealed record Observation(string Topic, string Expected, string? Reason, RouterVerdict Verdict);

    internal sealed record VectorMessage(string Name, string Expected, string? Reason, long TimeMs, ulong? SubnetId);

    internal sealed record VectorBlock(string Name, bool Failed, bool Pending = false, string? PayloadStatus = null, bool PayloadPresent = false);

    internal sealed record VectorMeta(string Topic, ulong? FinalizedEpoch, List<VectorMessage> Messages, List<VectorBlock> Blocks)
    {
        public static VectorMeta Load(string casePath)
        {
            using StreamReader reader = new(Path.Combine(casePath, "meta.yaml"));
            return Parse(reader);
        }

        /// <exception cref="InvalidDataException">The vector lists no messages, so it would check nothing.</exception>
        public static VectorMeta Parse(TextReader reader)
        {
            YamlStream yaml = [];
            yaml.Load(reader);
            YamlMappingNode root = (YamlMappingNode)yaml.Documents[0].RootNode;

            ulong? finalizedEpoch = root.Children.TryGetValue(new YamlScalarNode("finalized_checkpoint"), out YamlNode? checkpoint)
                ? ulong.Parse(Scalar((YamlMappingNode)checkpoint, "epoch")!)
                : null;

            long baseTime = long.Parse(Scalar(root, "current_time_ms") ?? "0");
            List<VectorMessage> messages = [];
            foreach (YamlMappingNode message in ((YamlSequenceNode)root.Children[new YamlScalarNode("messages")]).Children.Cast<YamlMappingNode>())
            {
                long time = Scalar(message, "current_time_ms") is { } absolute ? long.Parse(absolute) : baseTime + long.Parse(Scalar(message, "offset_ms") ?? "0");
                ulong? subnetId = Scalar(message, "subnet_id") is { } subnet ? ulong.Parse(subnet) : null;
                messages.Add(new VectorMessage(Scalar(message, "message")!, Scalar(message, "expected")!, Scalar(message, "reason"), time, subnetId));
            }

            if (messages.Count == 0)
                throw new InvalidDataException("meta.yaml lists no messages");

            List<VectorBlock> blocks = [];
            if (root.Children.TryGetValue(new YamlScalarNode("blocks"), out YamlNode? blockList))
            {
                foreach (YamlMappingNode block in ((YamlSequenceNode)blockList).Children.Cast<YamlMappingNode>())
                    blocks.Add(new VectorBlock(Scalar(block, "block")!, Scalar(block, "failed") == "true",
                        Scalar(block, "pending") == "true", Scalar(block, "payload_status"), Scalar(block, "payload") is not null));
            }

            return new VectorMeta(Scalar(root, "topic")!, finalizedEpoch, messages, blocks);
        }

        private static string? Scalar(YamlMappingNode node, string key) =>
            node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) ? ((YamlScalarNode)value).Value : null;
    }
}

public readonly record struct GossipValidationCase(string Preset, string Fork, string Handler, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}

internal enum RouterAction
{
    Raised,

    Deferred,

    Ignored,
    Rejected,
}

internal readonly record struct RouterVerdict(RouterAction Action, GossipDropReason? Drop = null)
{
    public static readonly RouterVerdict Raised = new(RouterAction.Raised);
    public static readonly RouterVerdict Deferred = new(RouterAction.Deferred);

    public static RouterVerdict Ignored(GossipDropReason drop) => new(RouterAction.Ignored, drop);
    public static RouterVerdict Rejected(GossipDropReason drop) => new(RouterAction.Rejected, drop);
    public override string ToString() => Drop is null ? Action.ToString() : $"{Action} as {Drop}";
}
