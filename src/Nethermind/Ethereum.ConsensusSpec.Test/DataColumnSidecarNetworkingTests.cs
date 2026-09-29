// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
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
using Nethermind.Core.Extensions;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the mainnet-preset consensus-specs <c>networking/gossip_data_column_sidecar</c> vectors against
/// <see cref="ColumnGossipRouter"/>, with the vector conventions of <see cref="GossipValidationTests"/>.
/// </summary>
/// <remarks>
/// A Fulu router reads fork choice, the key cache and the proposer lookahead seeded from each vector's anchor state and
/// blocks. A Fulu sidecar that passes is Accepted with its event raised; a Gloas one is Accepted. Both count as raised.
/// Every other verdict must match a row of <see cref="SynchronousVerdicts"/>, and an expected reject the router does
/// not reject makes the vector not-implemented, named by its reason.
/// </remarks>
[TestFixture]
public class DataColumnSidecarNetworkingTests
{
    private const string Suite = "networking";
    private const string Handler = "gossip_data_column_sidecar";
    private const string NotImplementedSentinel = "";

    private static readonly string[] Forks = ["fulu", "gloas"];
    private static readonly ColumnGossipDropReason[] DropReasons = Enum.GetValues<ColumnGossipDropReason>();

    /// <summary>The verdicts other than raised that <see cref="ColumnGossipRouter"/> reaches on the vectors' messages, per fork and vector <c>reason</c>.</summary>
    /// <remarks>Checked both ways: a message the router rejects or drops must match a row, and every row must be reached by a message.</remarks>
    private static readonly IReadOnlyDictionary<string, (string Reason, ColumnVerdict Verdict)[]> SynchronousVerdicts =
        new Dictionary<string, (string, ColumnVerdict)[]>(StringComparer.Ordinal)
        {
            ["fulu"] =
            [
                ("already seen sidecar from this proposer for this slot and index", ColumnVerdict.Ignored(ColumnGossipDropReason.Duplicate)),
                ("sidecar is from a future slot", ColumnVerdict.Ignored(ColumnGossipDropReason.FutureSlot)),
                ("sidecar is not from a slot greater than the latest finalized slot", ColumnVerdict.Ignored(ColumnGossipDropReason.BeforeFinalized)),
                ("invalid sidecar", ColumnVerdict.Rejected(ColumnGossipDropReason.FailedStructure)),
                ("sidecar is for wrong subnet", ColumnVerdict.Rejected(ColumnGossipDropReason.WrongSubnet)),
                // fulu/p2p-interface.md: answered from the store, which holds only accepted blocks.
                ("sidecar is not from a higher slot than its parent", ColumnVerdict.Rejected(ColumnGossipDropReason.NotAboveParentSlot)),
                // REJECTs needing no state that the spec orders after the parent checks; gossip_validation.md lets them run in any order.
                ("invalid sidecar inclusion proof", ColumnVerdict.Rejected(ColumnGossipDropReason.FailedInclusionProof)),
                ("invalid sidecar kzg proofs", ColumnVerdict.Rejected(ColumnGossipDropReason.FailedKzgProofs)),
                ("invalid proposer signature on sidecar block header", ColumnVerdict.Rejected(ColumnGossipDropReason.InvalidHeaderSignature)),
                ("finalized checkpoint is not an ancestor of sidecar's block", ColumnVerdict.Rejected(ColumnGossipDropReason.NotFinalizedDescendant)),
                ("sidecar proposer_index does not match expected proposer", ColumnVerdict.Rejected(ColumnGossipDropReason.UnexpectedProposer)),
                // The expected-proposer REJECT needs no BLS work, so it runs before the key cache is read.
                ("proposer index out of range", ColumnVerdict.Rejected(ColumnGossipDropReason.UnexpectedProposer)),
                // A parent fork choice does not hold cannot name the expected proposer; the MAY-queue IGNORE applies.
                ("sidecar's parent has not been seen", ColumnVerdict.Ignored(ColumnGossipDropReason.ProposerNotVerifiable)),
                // No record of blocks that failed validation is kept, so such a parent reads as not seen and the vector is reported not-implemented.
                ("sidecar's parent failed validation", ColumnVerdict.Ignored(ColumnGossipDropReason.ProposerNotVerifiable)),
            ],
            // A sidecar whose block is not held is parked, so "block ... failed validation" is Ignored and reported not-implemented.
            ["gloas"] =
            [
                ("already seen sidecar for this block root and index", ColumnVerdict.Ignored(ColumnGossipDropReason.Duplicate)),
                ("block for sidecar's beacon block root has not been seen", ColumnVerdict.Ignored(ColumnGossipDropReason.UnknownBlock)),
                ("block for sidecar's beacon block root failed validation", ColumnVerdict.Ignored(ColumnGossipDropReason.UnknownBlock)),
                ("sidecar is from a future slot", ColumnVerdict.Ignored(ColumnGossipDropReason.FutureSlot)),
                ("sidecar is for wrong subnet", ColumnVerdict.Rejected(ColumnGossipDropReason.WrongSubnet)),
                ("sidecar's slot does not match block's slot", ColumnVerdict.Rejected(ColumnGossipDropReason.SlotMismatch)),
                ("invalid sidecar", ColumnVerdict.Rejected(ColumnGossipDropReason.FailedStructure)),
                ("invalid sidecar kzg proofs", ColumnVerdict.Rejected(ColumnGossipDropReason.FailedKzgProofs)),
            ],
        };

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(GossipValidationCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord(Suite, testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    // A wrong suite path, a dropped extraction entry or an emptied case source enumerates zero vectors, and zero vectors run green.
    [Test]
    public void Every_fork_has_vectors_in_the_archive() =>
        Assert.That(TestedCases().Select(static c => c.Fork).Distinct(), Is.EquivalentTo(Forks));

    // Not-implemented vectors are Inconclusive, so a driver that reports every vector that way still runs green.
    [Test]
    public void Every_fork_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented()
    {
        List<GossipValidationCase> cases = TestedCases();
        IEnumerable<GossipValidationCase> sentinels = cases.DistinctBy(static c => c.Fork).Select(static c => c with { CasePath = NotImplementedSentinel });
        FuluDriverSupport.AssertEveryKeyRunsSomeVector([.. sentinels, .. cases], static c => c.Fork, static testCase =>
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
        HashSet<(string Fork, string Reason, ColumnVerdict Verdict)> reached = [];
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
                if (observed.Reason is not null && observed.Verdict.Action != RouterAction.Raised)
                    reached.Add((testCase.Fork, observed.Reason, observed.Verdict));
            }

            bool hasUncheckedReject = observations.Any(static o => o.Expected == "reject" && o.Verdict.Action != RouterAction.Rejected);
            if (reported != hasUncheckedReject)
                misreported.Add($"{testCase.VectorName} {(hasUncheckedReject ? "passed but has a reject the router does not reject" : "reported not-implemented")}");
        }

        IEnumerable<(string, string, ColumnVerdict)> rows = SynchronousVerdicts.SelectMany(static entry => entry.Value.Select(row => (entry.Key, row.Reason, row.Verdict)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(misreported, Is.Empty);
            Assert.That(reached, Is.EquivalentTo(rows));
        }
    }

    private static List<GossipValidationCase> TestedCases() =>
        FuluDriverSupport.TestedCases<GossipValidationCase>(ConsensusPreset.Mainnet, static () => [], MainnetCases);

    private static void Run(GossipValidationCase testCase, ICollection<Observation>? observations = null)
    {
        GossipValidationTests.VectorMeta meta = GossipValidationTests.VectorMeta.Load(testCase.CasePath);
        bool gloas = testCase.Fork == "gloas";
        string statePath = Path.Combine(testCase.CasePath, "state.ssz_snappy");
        (ulong genesisTime, ulong anchorFinalizedEpoch) = GossipValidationTests.ReadAnchorState(statePath, gloas);
        BeaconStateFulu? anchor = gloas ? null : FuluDriverSupport.DecodeState(statePath);
        BeaconChainSpec spec = GossipValidationTests.WithGenesisTime(GossipValidationTests.VectorSpec(testCase.CasePath, gloas), genesisTime);
        if (anchor is not null)
            spec = WithAnchorDomain(spec, anchor);
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
        SlotClock clock = new(spec, timestamper);
        BeaconChainStore store = GossipValidationTests.SeedStore(testCase.CasePath, spec);
        ColumnGossipRouter router;
        if (anchor is null)
        {
            router = new(spec, clock, LimboLogs.Instance, store: store, status: status);
        }
        else
        {
            (ForkChoiceSnapshot snapshot, ProposerLookaheadSnapshot lookahead) = SeedForkChoice(testCase.CasePath, meta, spec, anchor);
            router = new(spec, clock, LimboLogs.Instance, store: store, status: status,
                forkChoice: new ForkChoiceSnapshotHolder { Current = snapshot },
                pubkeys: FuluDriverSupport.BuildPubkeyCache(anchor.Validators!),
                proposerLookahead: new ProposerLookaheadHolder { Current = lookahead });
        }

        ulong[] subnets = [.. Enumerable.Range(0, (int)Eip7594DasConstants.DataColumnSidecarSubnetCount).Select(static s => (ulong)s)];
        router.Start(static _ => new NullTopic(), ForkDigest.Compute(spec, 0), subnets);
        int raised = 0;
        router.DataColumnSidecarReceived += _ => raised++;

        List<string> failures = [];
        List<string> uncheckedRejects = [];
        for (int i = 0; i < meta.Messages.Count; i++)
        {
            GossipValidationTests.VectorMessage message = meta.Messages[i];
            string label = $"message {i} ({message.Name}) expected {message.Expected}{(message.Reason is null ? "" : $" ({message.Reason})")}";
            long[] dropsBefore = [.. DropReasons.Select(router.GetDropCount)];
            int raisedBefore = raised;

            timestamper.UtcNow = genesis.AddMilliseconds(message.TimeMs);
            ulong subnetId = message.SubnetId ?? throw new InvalidDataException($"{label}: no subnet_id");
            MessageValidity validity = router.Handle(subnetId, gloas, File.ReadAllBytes(Path.Combine(testCase.CasePath, message.Name + ".ssz_snappy")));
            ColumnGossipDropReason[] drops = [.. DropReasons.Where((reason, index) => router.GetDropCount(reason) != dropsBefore[index])];

            ColumnVerdict? verdict = (drops, raised - raisedBefore, validity) switch
            {
                ([ColumnGossipDropReason.Oversized or ColumnGossipDropReason.InvalidSnappy or ColumnGossipDropReason.InvalidSsz], _, _) => null,
                ([ColumnGossipDropReason drop], 0, MessageValidity.Rejected) => ColumnVerdict.Rejected(drop),
                ([ColumnGossipDropReason drop], 0, MessageValidity.Ignored) => ColumnVerdict.Ignored(drop),
                ([], 1, MessageValidity.Accepted) when !gloas => ColumnVerdict.Raised,
                ([], 0, MessageValidity.Accepted) when gloas => ColumnVerdict.Raised,
                _ => null,
            };

            if (verdict is not { } actual)
            {
                failures.Add($"{label} but the router returned {validity} with drops [{string.Join(", ", drops)}] and {raised - raisedBefore} events");
                continue;
            }

            observations?.Add(new Observation(message.Expected, message.Reason, actual));
            if (actual != ColumnVerdict.Raised && (message.Reason is null || !IsSynchronousVerdict(testCase.Fork, message.Reason, actual)))
                failures.Add($"{label} but the router's verdict {actual} has no row in {nameof(SynchronousVerdicts)}");

            switch (message.Expected)
            {
                case "valid" when actual != ColumnVerdict.Raised:
                case "ignore" when actual.Action == RouterAction.Rejected:
                    failures.Add($"{label} but was {actual}");
                    break;
                case "valid" or "ignore":
                case "reject" when actual.Action == RouterAction.Rejected:
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
            throw new NotImplementedInDriverException($"ColumnGossipRouter does not reject: {string.Join("; ", uncheckedRejects)}");
    }

    /// <summary>The fork-choice store and head lookahead of <c>get_forkchoice_store(anchor_state, anchor_block)</c> over the vector's accepted blocks.</summary>
    /// <remarks>The anchor is the earliest accepted block; meta.yaml's <c>finalized_checkpoint</c>, when present, replaces the store's.</remarks>
    private static (ForkChoiceSnapshot Snapshot, ProposerLookaheadSnapshot Lookahead) SeedForkChoice(string casePath, GossipValidationTests.VectorMeta meta, BeaconChainSpec spec, BeaconStateFulu anchor)
    {
        ForkedSignedBeaconBlock[] blocks = [.. meta.Blocks
            .Where(static block => !block.Failed)
            .Select(block => SignedBeaconBlockCodec.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, block.Name + ".ssz_snappy")), spec))
            .OrderBy(static block => block.Slot)];
        if (blocks.Length == 0 || blocks[0].Slot != anchor.Slot)
            throw new InvalidDataException($"no accepted block at the anchor state's slot {anchor.Slot}");

        HashSet<Hash256> roots = [.. blocks.Select(static block => block.ComputeMessageRoot())];
        ForkChoiceSnapshotNode[] nodes = [.. blocks.Select(block => new ForkChoiceSnapshotNode(
            block.Slot, block.ComputeMessageRoot(), roots.Contains(block.ParentRoot) ? block.ParentRoot : null, 0, 0, 0, ExecutionStatus.Valid, Hash256.Zero))];
        Hash256 anchorRoot = nodes[0].Root;
        CheckpointRef finalized = new(meta.FinalizedEpoch ?? anchor.FinalizedCheckpoint!.Epoch, ReadFinalizedRoot(casePath) ?? anchorRoot);

        ulong epoch = anchor.GetCurrentEpoch();
        Hash256 dependentRoot = ProposerLookaheadSnapshot.FindDependentRoot(nodes, anchorRoot, BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch))!;
        return (new ForkChoiceSnapshot(finalized, finalized, Hash256.Zero, nodes), new ProposerLookaheadSnapshot(epoch, dependentRoot, anchor.ProposerLookahead!));
    }

    /// <summary>meta.yaml's <c>finalized_checkpoint</c> root, given as <c>root</c> or as the name of a <c>block_0x{root}</c> file.</summary>
    private static Hash256? ReadFinalizedRoot(string casePath)
    {
        using StreamReader reader = new(Path.Combine(casePath, "meta.yaml"));
        YamlStream yaml = [];
        yaml.Load(reader);
        YamlMappingNode root = (YamlMappingNode)yaml.Documents[0].RootNode;
        if (!root.Children.TryGetValue(new YamlScalarNode("finalized_checkpoint"), out YamlNode? node))
            return null;

        IDictionary<YamlNode, YamlNode> checkpoint = ((YamlMappingNode)node).Children;
        string hex = checkpoint.TryGetValue(new YamlScalarNode("root"), out YamlNode? rootNode) ? ((YamlScalarNode)rootNode).Value!
            : checkpoint.TryGetValue(new YamlScalarNode("block"), out YamlNode? blockNode) ? ((YamlScalarNode)blockNode).Value!["block_".Length..]
            : throw new InvalidDataException("finalized_checkpoint names neither a root nor a block");
        return new Hash256(Bytes.FromHexString(hex));
    }

    // get_domain(state, DOMAIN_BEACON_PROPOSER, epoch) reads the anchor state's fork and genesis_validators_root, not the config's.
    private static BeaconChainSpec WithAnchorDomain(BeaconChainSpec spec, BeaconStateFulu anchor) => new()
    {
        ChainId = spec.ChainId,
        CheckpointSyncUrl = spec.CheckpointSyncUrl,
        SecondsPerSlot = spec.SecondsPerSlot,
        SlotsPerEpoch = spec.SlotsPerEpoch,
        GenesisTime = spec.GenesisTime,
        GenesisValidatorsRoot = anchor.GenesisValidatorsRoot!,
        Forks = [new(anchor.Fork!.PreviousVersion!, 0), new(anchor.Fork.CurrentVersion!, anchor.Fork.Epoch)],
        BlobSchedule = spec.BlobSchedule,
        ElectraForkEpoch = spec.ElectraForkEpoch,
        FuluForkEpoch = spec.FuluForkEpoch,
        MaxBlobsPerBlockElectra = spec.MaxBlobsPerBlockElectra,
        GloasForkEpoch = spec.GloasForkEpoch,
        GloasForkVersion = spec.GloasForkVersion,
        Bootnodes = spec.Bootnodes,
    };

    private static bool IsSynchronousVerdict(string fork, string reason, ColumnVerdict verdict) =>
        SynchronousVerdicts.TryGetValue(fork, out (string Reason, ColumnVerdict Verdict)[]? rows) && rows.Contains((reason, verdict));

    private static IEnumerable<TestCaseData> MainnetCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled) yield break;

        foreach (string fork in Forks)
        {
            string? suitePath = ConsensusSpecArchive.SuitePath(ConsensusPreset.Mainnet, fork, Suite);
            string? handlerRoot = suitePath is null ? null : Path.Combine(suitePath, Handler);
            foreach (string caseDir in ConsensusSpecArchive.LeafDirs(handlerRoot, "meta.yaml"))
            {
                string vectorName = $"{ConsensusPreset.Mainnet}/{fork}/{Suite}/{Handler}/{Path.GetRelativePath(handlerRoot!, caseDir).Replace('\\', '/')}";
                yield return new TestCaseData(new GossipValidationCase(nameof(ConsensusPreset.Mainnet), fork, Handler, caseDir, vectorName)).SetName(vectorName);
            }
        }
    }

    /// <summary>A message's expected result and reason from meta.yaml, with the verdict the router reached.</summary>
    private sealed record Observation(string Expected, string? Reason, ColumnVerdict Verdict);

    /// <summary>A <see cref="RouterAction"/> with the drop reason <see cref="ColumnGossipRouter"/> counted, if any.</summary>
    private readonly record struct ColumnVerdict(RouterAction Action, ColumnGossipDropReason? Drop = null)
    {
        public static readonly ColumnVerdict Raised = new(RouterAction.Raised);

        public static ColumnVerdict Ignored(ColumnGossipDropReason drop) => new(RouterAction.Ignored, drop);

        public static ColumnVerdict Rejected(ColumnGossipDropReason drop) => new(RouterAction.Rejected, drop);

        public override string ToString() => Drop is null ? Action.ToString() : $"{Action} as {Drop}";
    }

    private sealed class NullTopic : ITopic
    {
        public event Action<byte[]>? OnMessage { add { } remove { } }

        public bool IsSubscribed => true;

        public void Subscribe() { }

        public void Unsubscribe() { }

        public void Publish(byte[] value) { }

        public void Publish(IMessage value) { }
    }
}
