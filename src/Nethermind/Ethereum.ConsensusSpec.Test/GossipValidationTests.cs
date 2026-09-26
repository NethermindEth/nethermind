// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the mainnet-preset consensus-specs <c>networking/gossip_*</c> vectors (tests/formats/networking/gossip_validation.md)
/// against the synchronous checks of <see cref="GossipRouter"/>, for <see cref="Suites"/>.
/// </summary>
/// <remarks>
/// <see cref="GossipRouter"/> raises a message that passes its stateless checks and returns Ignored; the signature and
/// state rules run later in the import pipeline. Each message's <see cref="RouterVerdict"/> is observed from the router's
/// events and drop counters, and every verdict other than raised must match a row of <see cref="SynchronousVerdicts"/>.
/// An expected <c>valid</c> must be raised, an expected <c>ignore</c> must never be Rejected, and an expected <c>reject</c>
/// must be Rejected. An expected reject the router raises or only drops makes the vector not-implemented, named by its reason.
/// The minimal preset is not enumerated: the containers and limits here are mainnet-preset-shaped.
/// </remarks>
[TestFixture]
public class GossipValidationTests
{
    private const string Suite = "networking";

    private const string NotImplementedSentinel = "";

    /// <summary>The <c>networking</c> handlers driven, per fork: exactly the topics <see cref="GossipRouter"/> validates on a Fulu or Gloas digest.</summary>
    internal static readonly (string Fork, string[] Handlers)[] Suites =
    [
        ("fulu", ["gossip_attester_slashing", "gossip_beacon_aggregate_and_proof", "gossip_beacon_block"]),
        ("gloas", ["gossip_attester_slashing", "gossip_beacon_aggregate_and_proof", "gossip_beacon_block", "gossip_execution_payload_envelope"]),
    ];

    /// <summary>The verdicts other than raised that <see cref="GossipRouter"/> reaches on the vectors' messages, per topic and vector <c>reason</c>.</summary>
    /// <remarks>
    /// Taken from the router's behaviour on the vectors at <see cref="ConsensusSpecArchive.Version"/> and checked against it
    /// both ways: a message the router rejects, drops or defers must match a row, and every row must be reached by a message.
    /// A reason the router reaches through more than one check has a row for each.
    /// </remarks>
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
                // REJECTs the router only drops, as it orders them after store checks; gossip_validation.md lets them run in any order.
                ("too many blob kzg commitments", RouterVerdict.Ignored(GossipDropReason.LimitExceeded)),
                ("bid's parent does not equal block's parent", RouterVerdict.Ignored(GossipDropReason.InvalidField)),
                ("block is not from a higher slot than its parent", RouterVerdict.Ignored(GossipDropReason.InvalidField)),
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
                // REJECTs the router only drops, as it orders them after store checks; gossip_validation.md lets them run in any order.
                ("attestation epoch does not match target epoch", RouterVerdict.Ignored(GossipDropReason.InvalidField)),
                ("aggregate has no participants", RouterVerdict.Ignored(GossipDropReason.InvalidField)),
            ],
            [GossipTopics.AttesterSlashing] =
            [
                ("all attester slashing indices already seen", RouterVerdict.Ignored(GossipDropReason.Duplicate)),
                ("all attester slashing indices already seen", RouterVerdict.Ignored(GossipDropReason.InvalidField)),
                // With no seen-index set the router cannot rule out the earlier IGNORE, so it only drops this REJECT.
                ("attestation data is not slashable", RouterVerdict.Ignored(GossipDropReason.InvalidField)),
            ],
            [GossipTopics.ExecutionPayload] =
            [
                // gloas/p2p-interface.md: verify_execution_requests_limits on the envelope.
                ("too many builder deposit requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many builder exit requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many consolidation requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("too many withdrawal requests", RouterVerdict.Rejected(GossipDropReason.LimitExceeded)),
                ("envelope is from a slot before the latest finalized slot", RouterVerdict.Ignored(GossipDropReason.BeforeFinalized)),
                ("already seen envelope for this block root from this builder", RouterVerdict.Ignored(GossipDropReason.Duplicate)),
                // A REJECT the router only drops, as it orders it after the block checks; gossip_validation.md lets it run in any order.
                ("too many withdrawals", RouterVerdict.Ignored(GossipDropReason.LimitExceeded)),
            ],
        };

    private static readonly GossipDropReason[] DropReasons = Enum.GetValues<GossipDropReason>();

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(GossipValidationCase testCase) => Execute(testCase);

    // A wrong suite path, a dropped extraction entry or an emptied case source enumerates zero vectors, and zero vectors run green.
    [Test]
    public void Every_fork_and_handler_has_vectors_in_the_archive()
    {
        IEnumerable<string> enumerated = TestedCases().Select(KeyOf).Distinct();
        IEnumerable<string> expected = Suites.SelectMany(static s => s.Handlers.Select(handler => $"{s.Fork}/{handler}"));
        Assert.That(enumerated, Is.EquivalentTo(expected));
    }

    // Not-implemented vectors are Inconclusive, so a driver that reports every vector that way still runs green.
    // Every handler has both kinds by design, so a not-implemented sentinel leads each handler and the check must look past it.
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

    // A row no message reaches is stale, and a vector that hides an unchecked reject behind a pass runs green.
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(misreported, Is.Empty);
            Assert.That(reached, Is.EquivalentTo(rows));
        }
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

    private static string KeyOf(GossipValidationCase testCase) => $"{testCase.Fork}/{testCase.Handler}";

    private static List<GossipValidationCase> TestedCases() =>
        FuluDriverSupport.TestedCases<GossipValidationCase>(ConsensusPreset.Mainnet, static () => [], MainnetCases);

    private static void Execute(GossipValidationCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord(Suite, testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    /// <param name="testCase">The vector to run.</param>
    /// <param name="observations">Receives each decoded message's expected result, reason and observed verdict.</param>
    private static void Run(GossipValidationCase testCase, ICollection<Observation>? observations = null)
    {
        VectorMeta meta = VectorMeta.Load(testCase.CasePath);
        bool gloas = testCase.Fork == "gloas";
        BeaconChainSpec spec = VectorSpec(testCase.CasePath, gloas);
        long slotMs = (long)spec.SecondsPerSlot * 1000;
        DateTime genesis = DateTimeOffset.FromUnixTimeSeconds((long)spec.GenesisTime).UtcDateTime;
        ManualTimestamper timestamper = new(genesis);
        BeaconChainStatusHolder status = new(spec, timestamper)
        {
            CurrentStatus = new StatusMessageV2
            {
                FinalizedEpoch = meta.FinalizedEpoch ?? AnchorFinalizedEpoch(Path.Combine(testCase.CasePath, "state.ssz_snappy"), gloas),
                FinalizedRoot = Hash256.Zero,
                HeadRoot = Hash256.Zero,
            },
        };
        GossipRouter router = new(spec, new SlotClock(spec, timestamper), LimboLogs.Instance, status: status);
        int raised = 0;
        router.BeaconBlockReceived += _ => raised++;
        router.AggregateAndProofReceived += _ => raised++;
        router.GloasAggregateAndProofReceived += _ => raised++;
        router.AttesterSlashingReceived += _ => raised++;
        router.GloasAttesterSlashingReceived += _ => raised++;
        router.ExecutionPayloadEnvelopeReceived += _ => raised++;

        List<string> failures = [];
        List<string> uncheckedRejects = [];
        for (int i = 0; i < meta.Messages.Count; i++)
        {
            VectorMessage message = meta.Messages[i];
            string label = $"message {i} ({message.Name}) expected {message.Expected}{(message.Reason is null ? "" : $" ({message.Reason})")}";
            long[] dropsBefore = [.. DropReasons.Select(router.GetDropCount)];
            int raisedBefore = raised;

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

    private static bool IsSynchronousVerdict(string topic, string reason, RouterVerdict verdict) =>
        SynchronousVerdicts.TryGetValue(topic, out (string Reason, RouterVerdict Verdict)[]? rows) && rows.Contains((reason, verdict));

    /// <summary>The vector's own <c>config.yaml</c> when present, otherwise the mainnet config with the vector's fork live from genesis.</summary>
    /// <exception cref="NotImplementedInDriverException">The config's slot duration differs from the spec's, which <see cref="SlotClock"/> would misread.</exception>
    private static BeaconChainSpec VectorSpec(string casePath, bool gloas)
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

    private static ulong AnchorFinalizedEpoch(string statePath, bool gloas)
    {
        if (!gloas)
            return FuluDriverSupport.DecodeState(statePath).FinalizedCheckpoint!.Epoch;

        BeaconStateGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(statePath), out BeaconStateGloas state);
        return state.FinalizedCheckpoint!.Epoch;
    }

    private static IEnumerable<TestCaseData> MainnetCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled) yield break;

        foreach ((string fork, string[] handlers) in Suites)
        {
            string? suitePath = ConsensusSpecArchive.SuitePath(ConsensusPreset.Mainnet, fork, Suite);
            foreach (string handler in handlers)
            {
                string? handlerRoot = suitePath is null ? null : Path.Combine(suitePath, handler);
                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(handlerRoot, "meta.yaml"))
                {
                    string vectorName = $"{ConsensusPreset.Mainnet}/{fork}/{Suite}/{handler}/{Path.GetRelativePath(handlerRoot!, caseDir).Replace('\\', '/')}";
                    yield return new TestCaseData(new GossipValidationCase(nameof(ConsensusPreset.Mainnet), fork, handler, caseDir, vectorName)).SetName(vectorName);
                }
            }
        }
    }

    /// <summary>A message's expected result and reason from meta.yaml, with the verdict the router reached.</summary>
    private sealed record Observation(string Topic, string Expected, string? Reason, RouterVerdict Verdict);

    /// <summary>One message of meta.yaml's <c>messages</c>, received at <see cref="TimeMs"/> after genesis.</summary>
    private sealed record VectorMessage(string Name, string Expected, string? Reason, long TimeMs);

    /// <summary>The meta.yaml fields the synchronous checks read; the store-building <c>blocks</c> are not among them.</summary>
    private sealed record VectorMeta(string Topic, ulong? FinalizedEpoch, List<VectorMessage> Messages)
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
                messages.Add(new VectorMessage(Scalar(message, "message")!, Scalar(message, "expected")!, Scalar(message, "reason"), time));
            }

            if (messages.Count == 0)
                throw new InvalidDataException("meta.yaml lists no messages");

            return new VectorMeta(Scalar(root, "topic")!, finalizedEpoch, messages);
        }

        private static string? Scalar(YamlMappingNode node, string key) =>
            node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) ? ((YamlScalarNode)value).Value : null;
    }
}

public readonly record struct GossipValidationCase(string Preset, string Fork, string Handler, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}

/// <summary>What <see cref="GossipRouter"/> did with a message.</summary>
internal enum RouterAction
{
    /// <summary>Its typed event was raised.</summary>
    Raised,

    /// <summary>It was held and raised once the next slot started.</summary>
    Deferred,

    Ignored,
    Rejected,
}

/// <summary>A <see cref="RouterAction"/> with the drop reason the router counted, if any.</summary>
internal readonly record struct RouterVerdict(RouterAction Action, GossipDropReason? Drop = null)
{
    public static readonly RouterVerdict Raised = new(RouterAction.Raised);
    public static readonly RouterVerdict Deferred = new(RouterAction.Deferred);

    public static RouterVerdict Ignored(GossipDropReason drop) => new(RouterAction.Ignored, drop);

    public static RouterVerdict Rejected(GossipDropReason drop) => new(RouterAction.Rejected, drop);

    public override string ToString() => Drop is null ? Action.ToString() : $"{Action} as {Drop}";
}
