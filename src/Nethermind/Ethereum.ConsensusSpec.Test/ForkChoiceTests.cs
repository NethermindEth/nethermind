// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Snappier;

namespace Ethereum.ConsensusSpec.Test;

/// <remarks>Mainnet SSZ bounds prevent minimal-state decoding; minimal vectors are reported not-implemented. Mainnet requires NETHERMIND_CONSENSUS_SPEC_MAINNET=1.</remarks>
[TestFixture]
public class ForkChoiceTests
{
    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(ForkChoiceCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(ForkChoiceCase testCase) => Execute(testCase);

    private static readonly IReadOnlyDictionary<ConsensusPreset, string[]> HandlersByPreset = new Dictionary<ConsensusPreset, string[]>
    {
        [ConsensusPreset.Minimal] = ["deposit_with_reorg", "ex_ante", "get_head", "get_proposer_head", "on_block", "reorg", "withholding"],
        [ConsensusPreset.Mainnet] = ["ex_ante", "get_head", "get_proposer_head", "on_block"],
    };
    // The fork set is not asserted: Cases reads fulu only, and GloasForkChoiceTests reads gloas.
    [Test]
    public void Every_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<ForkChoiceCase> cases = FuluDriverSupport.TestedCases<ForkChoiceCase>(preset, MinimalCases, MainnetCases);
        Assert.That(cases.Select(HandlerOf).Distinct(), Is.EquivalentTo(HandlersByPreset[preset]));
    }
    [Test]
    public void Every_handler_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<ForkChoiceCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases), HandlerOf, Run);

    private const string FabricationSource = "get_proposer_head/pyspec_tests/basic_is_parent_root";

    private const string AttestingBlock = "block_0x9efbe54f7970373161723e08fb309354c6cfab3cfa15b003c2f9af393f150c43";

    private const ulong TickAtBlockSlot = 33 * 12;

    /// <summary>Pins optimistic-only body replay for an EL-INVALID refused block (pyspec helpers/fork_choice.py).</summary>
    /// <remarks>Both paths exclude the invalidated block and refuse its child (specs/bellatrix/optimistic-sync.md).</remarks>
    [TestCaseSource(nameof(RefusedBlockCases))]
    public void Refused_block_step_replays_its_body_only_when_refused_optimistically(string refusal, bool executionValid, ulong tick, bool optimistic)
    {
        byte[] block = ReadFabricationBlock(AttestingBlock);
        SignedBeaconBlock.Decode(block, out SignedBeaconBlock refused);
        SignedBeaconBlock.Decode(block, out SignedBeaconBlock child);
        child.Message!.ParentRoot = SszRoots.HashTreeRoot(refused.Message!);
        child.Message.Slot++;
        List<string> steps = [$"{{tick: {tick}}}"];
        if (optimistic)
        {
            Hash256 refusedPayload = refused.Message!.Body!.ExecutionPayload!.BlockHash!;
            BeaconBlock.Decode(ReadFabricationBlock("anchor_block"), out BeaconBlock anchor);
            steps.Add($"{{block_hash: '{refusedPayload}', payload_status: {{status: INVALID, latest_valid_hash: '{anchor.Body!.ExecutionPayload!.BlockHash}', validation_error: invalid}}}}");
        }

        steps.Add("{block: block_refused, valid: false}");
        steps.Add("{block: block_child, valid: false}");

        (ForkChoiceRunner runner, Hash256 anchorRoot, List<Exception?> rejections) = RunFabricatedCase(executionValid, [.. steps], ("block_refused", block), ("block_child", SignedBeaconBlock.Encode(child)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejections[^1], Is.TypeOf<ForkChoiceException>().With.Message.Contains("unknown to fork choice"), $"{refusal}: on_block's own parent check refuses the child");
            Assert.That(runner.GetHead(), Is.EqualTo(anchorRoot), $"{refusal}: a refused block must not enter the store");
            Assert.That(runner.Snapshot().Nodes.Single(n => n.Root == anchorRoot).Weight, optimistic ? Is.Positive : Is.Zero, $"{refusal}: body attestations replayed");
        }
    }

    private static IEnumerable<TestCaseData> RefusedBlockCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled)
            yield break;
        yield return new TestCaseData("the execution layer refuses the payload", false, TickAtBlockSlot, false);
        yield return new TestCaseData("fork choice refuses a block from a future slot", true, 0ul, false);
        yield return new TestCaseData("on_payload_info declares the payload INVALID", true, TickAtBlockSlot, true);
    }

    private static string FabricationSourcePath =>
        Path.Combine(ConsensusSpecArchive.SuitePath(ConsensusPreset.Mainnet, "fulu", "fork_choice")!, FabricationSource);

    private static byte[] ReadFabricationBlock(string key) =>
        SszConsensusTestLoader.ReadSszSnappy(Path.Combine(FabricationSourcePath, key + ".ssz_snappy"));

    private static (ForkChoiceRunner Runner, Hash256 AnchorRoot, List<Exception?> BlockRejections) RunFabricatedCase(bool executionValid, string[] steps, params (string Key, byte[] Ssz)[] blocks)
    {
        DirectoryInfo casePath = Directory.CreateTempSubdirectory("fork-choice-case");
        try
        {
            File.Copy(Path.Combine(FabricationSourcePath, "anchor_state.ssz_snappy"), Path.Combine(casePath.FullName, "anchor_state.ssz_snappy"));
            File.Copy(Path.Combine(FabricationSourcePath, "anchor_block.ssz_snappy"), Path.Combine(casePath.FullName, "anchor_block.ssz_snappy"));
            foreach ((string key, byte[] ssz) in blocks)
                File.WriteAllBytes(Path.Combine(casePath.FullName, key + ".ssz_snappy"), Snappy.CompressToArray(ssz));
            File.WriteAllText(Path.Combine(casePath.FullName, "execution.yaml"), $"{{execution_valid: {(executionValid ? "true" : "false")}}}\n");
            File.WriteAllText(Path.Combine(casePath.FullName, "steps.yaml"), string.Concat(steps.Select(static step => $"- {step}\n")));

            ForkChoiceRunner runner = ForkChoiceStepDriver.Run(casePath.FullName, out List<Exception?> rejections);
            BeaconBlock.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath.FullName, "anchor_block.ssz_snappy")), out BeaconBlock anchorBlock);
            return (runner, SszRoots.HashTreeRoot(anchorBlock), rejections);
        }
        finally
        {
            casePath.Delete(recursive: true);
        }
    }

    private static string HandlerOf(ForkChoiceCase testCase) => testCase.VectorName.Split('/')[3];

    private static void Execute(ForkChoiceCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("fork_choice", "fulu", testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(ForkChoiceCase testCase)
    {
        if (testCase.Preset == nameof(ConsensusPreset.Minimal))
        {
            throw new NotImplementedInDriverException(
                "BeaconStateFulu's SSZ shape hard-codes mainnet-preset-scaled vector bounds (see SszStaticTests' " +
                "BeaconState/Attestation/SyncCommittee entries), so it cannot decode a minimal-preset " +
                "anchor_state.ssz_snappy at all; this suite only drives fork choice for real against the " +
                "mainnet preset (opt in with NETHERMIND_CONSENSUS_SPEC_MAINNET=1).");
        }

        ForkChoiceStepDriver.Run(testCase.CasePath);
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.RelativeCases(preset, ["fulu"], "fork_choice", "manifest.yaml",
            static (p, fork, path, name) => new ForkChoiceCase(p.ToString(), path, name));
}

public readonly record struct ForkChoiceCase(string Preset, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
