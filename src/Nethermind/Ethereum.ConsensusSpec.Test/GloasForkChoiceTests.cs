// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using Snappier;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the Gloas <c>fork_choice</c> suite through <see cref="GloasForkChoiceStepDriver"/>. The minimal-preset vectors are
/// enumerated and reported not implemented, for the reason <see cref="ForkChoiceTests"/> gives: the Gloas state's SSZ shape
/// hard-codes mainnet-preset bounds.
/// </summary>
/// <remarks>
/// A vector that needs a payload status, a PTC vote or <c>on_payload_attestation_message</c> is reported not implemented,
/// but only after every step of it ran and every other check passed. A head root check is waived only for the vectors
/// in <see cref="HeadNeedsPayloadStatus"/>, and each of them must still diverge.
/// </remarks>
[TestFixture]
public class GloasForkChoiceTests
{
    private static readonly string[] MainnetHandlers =
    [
        "ex_ante", "get_head", "get_parent_payload_status", "on_attestation", "on_block", "on_execution_payload_envelope",
        "on_payload_attestation_message", "payload_data_availability", "payload_timeliness",
    ];

    /// <summary>The Gloas fork_choice handlers each preset carries at <see cref="ConsensusSpecArchive.Version"/>.</summary>
    private static readonly IReadOnlyDictionary<ConsensusPreset, string[]> HandlersByPreset = new Dictionary<ConsensusPreset, string[]>
    {
        [ConsensusPreset.Minimal] = [.. MainnetHandlers, "deposit_with_reorg", "reorg", "should_apply_proposer_boost", "withholding"],
        [ConsensusPreset.Mainnet] = MainnetHandlers,
    };

    /// <summary>
    /// Vectors whose expected head is a parent's FULL node chosen over a child that builds on its EMPTY node; the runner's
    /// block-root head picks the child (specs/gloas/fork-choice.md get_head, get_node_children).
    /// </summary>
    private static readonly HashSet<string> HeadNeedsPayloadStatus = new(StringComparer.Ordinal)
    {
        "Mainnet/gloas/fork_choice/get_head/pyspec_tests/get_head_full_payload_tiebreak",
        "Mainnet/gloas/fork_choice/on_attestation/pyspec_tests/validate_on_attestation_beacon_root_payload_check",
        "Mainnet/gloas/fork_choice/payload_data_availability/pyspec_tests/payload_data_availability_above_threshold_returns_true",
        "Mainnet/gloas/fork_choice/payload_timeliness/pyspec_tests/payload_timeliness_above_threshold_returns_true",
    };

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(ForkChoiceCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(ForkChoiceCase testCase) => Execute(testCase);

    // A wrong suite path or an emptied case source enumerates zero vectors, and zero vectors run green.
    [Test]
    public void Every_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<ForkChoiceCase> cases = FuluDriverSupport.TestedCases<ForkChoiceCase>(preset, MinimalCases, MainnetCases);
        Assert.That(cases.Select(HandlerOf).Distinct(), Is.EquivalentTo(HandlersByPreset[preset]));
    }

    // A driver that gives up at the first unsupported check still reports every vector not implemented, and so runs green.
    [Test]
    public void Every_handler_runs_every_step_of_a_vector()
    {
        List<ForkChoiceCase> cases = FuluDriverSupport.TestedCases<ForkChoiceCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases);
        Assert.That(cases, Is.Not.Empty, "no vectors are enumerated");
        foreach (IGrouping<string, ForkChoiceCase> byHandler in cases.GroupBy(HandlerOf, StringComparer.Ordinal))
        {
            ForkChoiceCase first = byHandler.First();
            Assert.That(() => RunSteps(first), Throws.Nothing, $"'{byHandler.Key}' does not run every step of {first}");
        }
    }

    /// <summary>The handlers with a vector that needs no PTC vote and no weighing of FULL against EMPTY nodes.</summary>
    private static readonly string[] DecidedHandlers =
    [
        "ex_ante", "get_head", "get_parent_payload_status", "on_attestation", "on_block", "on_execution_payload_envelope",
    ];

    // A driver that reports a check unsupported when the runner's store decides it runs such vectors green without checking them.
    [Test]
    public void Every_handler_the_runner_decides_passes_a_vector_outright()
    {
        List<ForkChoiceCase> cases = FuluDriverSupport.TestedCases<ForkChoiceCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases);
        IEnumerable<string> decided = cases.Where(static c => !HeadNeedsPayloadStatus.Contains(c.VectorName) && RunSteps(c).Unsupported.Count == 0)
            .Select(HandlerOf).Distinct();
        Assert.That(decided, Is.EquivalentTo(DecidedHandlers));
    }

    // Counting an undecided status as held passes a vector the runner never checked; counting a decided one undecided skips it.
    [TestCase(GloasForkChoiceStepDriver.PayloadStatusEmpty, false, "Holds")]
    [TestCase(GloasForkChoiceStepDriver.PayloadStatusEmpty, true, "Undecided")]
    [TestCase(GloasForkChoiceStepDriver.PayloadStatusFull, true, "Undecided")]
    [TestCase(GloasForkChoiceStepDriver.PayloadStatusFull, false, "Contradicted")]
    [TestCase(GloasForkChoiceStepDriver.PayloadStatusPending, false, "Contradicted")]
    [TestCase(GloasForkChoiceStepDriver.PayloadStatusPending, true, "Contradicted")]
    public void Head_payload_status_is_decided_only_without_a_full_node(byte expected, bool payloadVerified, string verdict) =>
        Assert.That(GloasForkChoiceStepDriver.DecideHeadPayloadStatus(expected, payloadVerified).ToString(), Is.EqualTo(verdict));

    /// <summary>The mainnet vector whose anchor, slot-1 block and verified payload the tests below reuse.</summary>
    private const string FabricationSource = "on_attestation/pyspec_tests/validate_on_attestation_later_slot_full_vote_valid";

    /// <summary>Slot 1, a child of the anchor whose payload envelope the vector delivers.</summary>
    private const string SlotOneBlock = "block_0x76bf14e70fc96a5a3442a46dff3bfb897d5568cdb4dc7e94419f625df2fcd9e5";

    // A waiver that ignores ancestry passes a vector whose expected head is a verified block on another branch or below the runner's head.
    [TestCaseSource(nameof(MainnetOnly))]
    public void Head_check_waiver_refuses_a_verified_full_node_that_is_not_an_ancestor(string casePath)
    {
        ForkChoiceRunner runner = GloasForkChoiceStepDriver.Run(casePath).Runner;
        Hash256 anchorRoot = AnchorRoot(casePath);
        Hash256 slotOne = new(SlotOneBlock["block_".Length..]);
        Assert.That(runner.IsPayloadVerified(slotOne), Is.True, "fixture bug: the vector delivers the slot-1 envelope");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GloasForkChoiceStepDriver.IsWaivedHeadDivergence(runner, expectsFull: true, slotOne, anchorRoot), Is.False, "a descendant of the head is not an ancestor");
            Assert.That(GloasForkChoiceStepDriver.IsWaivedHeadDivergence(runner, expectsFull: true, slotOne, slotOne), Is.False, "the head is not its own strict ancestor");
            Assert.That(GloasForkChoiceStepDriver.IsWaivedHeadDivergence(runner, expectsFull: false, anchorRoot, slotOne), Is.False, "only a FULL node is waived");
        }
    }

    /// <summary>
    /// The pyspec harness's <c>add_block</c> replays a block's body attestations and attester slashings into fork choice
    /// (tests/core/pyspec/eth_consensus_specs/test/helpers/fork_choice.py). No Gloas vector at the pinned tag carries either, so the
    /// slot-1 block is re-bodied with a vote of slot 0's committee for the anchor and a double vote of one member.
    /// The anchor's own weight, beyond what it inherits from the boosted child, is then the committee less the slashed member.
    /// </summary>
    [TestCaseSource(nameof(MainnetOnly))]
    public void Block_body_attestations_and_attester_slashings_are_replayed_into_fork_choice(string casePath)
    {
        BeaconStateGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "anchor_state.ssz_snappy")), out BeaconStateGloas anchorState);
        SignedBeaconBlockGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, SlotOneBlock + ".ssz_snappy")), out SignedBeaconBlockGloas signedBlock);
        Hash256 anchorRoot = AnchorRoot(casePath);
        BeaconChainSpec spec = FuluDriverSupport.TransitionSpec(0);

        BeaconStateGloas postState = anchorState.Clone();
        EpochCache cache = new();
        GloasSlotProcessing.ProcessSlots(postState, 1, cache);
        int[] committee = cache.GetCommitteeCache(postState, 0).GetBeaconCommittee(0, 0).ToArray();
        Assert.That(committee, Has.Length.GreaterThan(1), "fixture bug: the vote needs a member left after the slashing");

        AttestationData vote = new()
        {
            Slot = 0,
            Index = 0,
            BeaconBlockRoot = anchorRoot,
            Source = anchorState.CurrentJustifiedCheckpoint,
            Target = new Checkpoint { Epoch = 0, Root = anchorRoot },
        };
        AttestationData conflictingVote = new() { Slot = 0, Index = 0, BeaconBlockRoot = Keccak.OfAnEmptyString, Source = vote.Source, Target = vote.Target };
        BitArray committeeBits = new(Presets.MaxCommitteesPerSlot) { [0] = true };
        BeaconBlockGloas block = signedBlock.Message!;
        block.Body!.Attestations = [new AttestationGloas { AggregationBits = new BitArray(committee.Length, true), Data = vote, CommitteeBits = committeeBits }];
        ulong slashed = (ulong)committee[0];
        block.Body.AttesterSlashings =
        [
            new AttesterSlashingGloas
            {
                Attestation1 = new IndexedAttestationGloas { AttestingIndices = [slashed], Data = vote },
                Attestation2 = new IndexedAttestationGloas { AttestingIndices = [slashed], Data = conflictingVote },
            },
        ];
        GloasBlockProcessing.ProcessBlock(postState, block, cache, FuluDriverSupport.BuildPubkeyCache(anchorState.Validators!), new GloasForkChoiceStepDriver.ValidPayloadNotifier(), spec, verifySignatures: false);
        block.StateRoot = SszRoots.HashTreeRoot(postState);
        Hash256 blockRoot = SszRoots.HashTreeRoot(block);

        ForkChoiceRunner runner = RunFabricatedCase(casePath, ["{tick: 12}", "{block: block_attesting, valid: true}"], ("block_attesting", SignedBeaconBlockGloas.Encode(signedBlock)));
        runner.GetHead();
        IReadOnlyList<ForkChoiceSnapshotNode> nodes = runner.Snapshot().Nodes;
        ulong anchorOwnWeight = nodes.Single(n => n.Root == anchorRoot).Weight - nodes.Single(n => n.Root == blockRoot).Weight;
        ulong expected = committee.Where(i => (ulong)i != slashed).Aggregate(0ul, (sum, i) => sum + anchorState.Validators![i].EffectiveBalance);

        Assert.That(anchorOwnWeight, Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> MainnetOnly()
    {
        if (ConsensusSpecArchive.MainnetEnabled)
            yield return new TestCaseData(FabricationSourcePath).SetArgDisplayNames(FabricationSource);
    }

    private static string FabricationSourcePath =>
        Path.Combine(ConsensusSpecArchive.SuitePath(ConsensusPreset.Mainnet, "gloas", "fork_choice")!, FabricationSource);

    private static Hash256 AnchorRoot(string casePath)
    {
        BeaconBlockGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "anchor_block.ssz_snappy")), out BeaconBlockGloas anchorBlock);
        return SszRoots.HashTreeRoot(anchorBlock);
    }

    /// <summary>Runs <paramref name="steps"/> from the anchor of <paramref name="sourcePath"/> with signatures unchecked, with <paramref name="blocks"/> as the case's block files.</summary>
    private static ForkChoiceRunner RunFabricatedCase(string sourcePath, string[] steps, params (string Key, byte[] Ssz)[] blocks)
    {
        DirectoryInfo casePath = Directory.CreateTempSubdirectory("gloas-fork-choice-case");
        try
        {
            File.Copy(Path.Combine(sourcePath, "anchor_state.ssz_snappy"), Path.Combine(casePath.FullName, "anchor_state.ssz_snappy"));
            File.Copy(Path.Combine(sourcePath, "anchor_block.ssz_snappy"), Path.Combine(casePath.FullName, "anchor_block.ssz_snappy"));
            foreach ((string key, byte[] ssz) in blocks)
                File.WriteAllBytes(Path.Combine(casePath.FullName, key + ".ssz_snappy"), Snappy.CompressToArray(ssz));
            File.WriteAllText(Path.Combine(casePath.FullName, "meta.yaml"), "{bls_setting: 2}\n");
            File.WriteAllText(Path.Combine(casePath.FullName, "steps.yaml"), string.Concat(steps.Select(static step => $"- {step}\n")));
            return GloasForkChoiceStepDriver.Run(casePath.FullName).Runner;
        }
        finally
        {
            casePath.Delete(recursive: true);
        }
    }

    /// <summary>The handler directory, the segment after <c>{preset}/gloas/fork_choice/</c> in the vector name.</summary>
    private static string HandlerOf(ForkChoiceCase testCase) => testCase.VectorName.Split('/')[3];

    private static void Execute(ForkChoiceCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("fork_choice", "gloas", testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(ForkChoiceCase testCase)
    {
        if (testCase.Preset == nameof(ConsensusPreset.Minimal))
            throw new NotImplementedInDriverException("BeaconStateGloas's SSZ shape hard-codes mainnet-preset bounds, so it cannot decode a minimal-preset anchor_state.ssz_snappy.");

        GloasForkChoiceRun run = RunSteps(testCase);
        if (run.Unsupported.Count > 0)
        {
            throw new NotImplementedInDriverException(
                $"every step ran and every other check passed; fork choice has no entry point for {string.Join(", ", run.Unsupported)} (specs/gloas/fork-choice.md).");
        }
    }

    /// <summary>Runs every step, and fails a vector listed in <see cref="HeadNeedsPayloadStatus"/> whose head no longer diverges.</summary>
    private static GloasForkChoiceRun RunSteps(ForkChoiceCase testCase)
    {
        bool listed = HeadNeedsPayloadStatus.Contains(testCase.VectorName);
        GloasForkChoiceRun run = GloasForkChoiceStepDriver.Run(testCase.CasePath, listed);
        if (listed && run.HeadDivergences.Count == 0)
            Assert.Fail($"{testCase} no longer diverges from the runner's head; remove it from {nameof(HeadNeedsPayloadStatus)}");
        return run;
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        string? root = ConsensusSpecArchive.SuitePath(preset, "gloas", "fork_choice");
        if (root is null)
            yield break;

        foreach (string caseDir in ConsensusSpecArchive.LeafDirs(root, "manifest.yaml"))
        {
            string vectorName = $"{preset}/gloas/fork_choice/{Path.GetRelativePath(root, caseDir).Replace('\\', '/')}";
            yield return new TestCaseData(new ForkChoiceCase(preset.ToString(), caseDir, vectorName)).SetName(vectorName);
        }
    }
}
