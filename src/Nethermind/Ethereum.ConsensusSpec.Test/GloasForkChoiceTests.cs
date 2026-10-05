// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Snappier;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class GloasForkChoiceTests
{
    private static readonly string[] MainnetHandlers =
    [
        "ex_ante", "get_head", "get_parent_payload_status", "on_attestation", "on_block", "on_execution_payload_envelope",
        "on_payload_attestation_message", "payload_data_availability", "payload_timeliness",
    ];

    private static readonly IReadOnlyDictionary<ConsensusPreset, string[]> HandlersByPreset = new Dictionary<ConsensusPreset, string[]>
    {
        [ConsensusPreset.Minimal] = [.. MainnetHandlers, "deposit_with_reorg", "reorg", "should_apply_proposer_boost", "withholding"],
        [ConsensusPreset.Mainnet] = MainnetHandlers,
    };

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(ForkChoiceCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(ForkChoiceCase testCase) => Execute(testCase);
    [Test]
    public void Every_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<ForkChoiceCase> cases = FuluDriverSupport.TestedCases<ForkChoiceCase>(preset, MinimalCases, MainnetCases);
        Assert.That(cases.Select(HandlerOf).Distinct(), Is.EquivalentTo(HandlersByPreset[preset]));
    }

    // A vector the driver cannot run is reported not implemented, which runs green; each handler must pass one outright.
    [Test]
    public void Every_handler_passes_a_vector_outright()
    {
        List<ForkChoiceCase> cases = FuluDriverSupport.TestedCases<ForkChoiceCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases);
        Assert.That(cases, Is.Not.Empty, "no vectors are enumerated");
        foreach (IGrouping<string, ForkChoiceCase> byHandler in cases.GroupBy(HandlerOf, StringComparer.Ordinal))
        {
            ForkChoiceCase first = byHandler.First();
            Assert.That(() => GloasForkChoiceStepDriver.Run(first.CasePath), Throws.Nothing, $"'{byHandler.Key}' does not pass {first}");
        }
    }

    private const string FabricationSource = "on_attestation/pyspec_tests/validate_on_attestation_later_slot_full_vote_valid";

    private const string SlotOneBlock = "block_0x76bf14e70fc96a5a3442a46dff3bfb897d5568cdb4dc7e94419f625df2fcd9e5";

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
            return GloasForkChoiceStepDriver.Run(casePath.FullName);
        }
        finally
        {
            casePath.Delete(recursive: true);
        }
    }

    private static string HandlerOf(ForkChoiceCase testCase) => testCase.VectorName.Split('/')[3];

    private static void Execute(ForkChoiceCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("fork_choice", "gloas", testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(ForkChoiceCase testCase)
    {
        if (testCase.Preset == nameof(ConsensusPreset.Minimal))
            throw new NotImplementedInDriverException("BeaconStateGloas's SSZ shape hard-codes mainnet-preset bounds, so it cannot decode a minimal-preset anchor_state.ssz_snappy.");

        GloasForkChoiceStepDriver.Run(testCase.CasePath);
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.RelativeCases(preset, ["gloas"], "fork_choice", "manifest.yaml",
            static (p, fork, path, name) => new ForkChoiceCase(p.ToString(), path, name));
}
