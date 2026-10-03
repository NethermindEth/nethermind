// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using Snappier;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>fork_choice</c> suite by replaying each vector's <c>steps.yaml</c>
/// script (<see cref="ForkChoiceStepDriver"/>) against this repo's
/// <see cref="Nethermind.BeaconChain.ForkChoice.ForkChoiceRunner"/>. Fulu-only, mainnet-preset-only
/// for the same reason as <see cref="SanityTests"/> and <see cref="OperationsTests"/>:
/// <c>BeaconStateFulu</c>'s SSZ shape hard-codes mainnet-scaled vector bounds (sync committee size,
/// historical roots, randao mixes, slashings), so it cannot decode a minimal-preset
/// <c>anchor_state.ssz_snappy</c> at all - confirmed directly (decoding one throws
/// <c>InvalidDataException: expected at least 2737225 bytes but found 19921</c>). The minimal-preset
/// vectors (67 of them) are still enumerated and named, and reported not-implemented rather than
/// silently skipped; only the mainnet-preset vectors (opt in with
/// NETHERMIND_CONSENSUS_SPEC_MAINNET=1) actually drive the fork-choice pipeline.
/// </summary>
[TestFixture]
public class ForkChoiceTests
{
    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(ForkChoiceCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(ForkChoiceCase testCase) => Execute(testCase);

    /// <summary>The fork_choice handlers each preset carries at <see cref="ConsensusSpecArchive.Version"/>.</summary>
    private static readonly IReadOnlyDictionary<ConsensusPreset, string[]> HandlersByPreset = new Dictionary<ConsensusPreset, string[]>
    {
        [ConsensusPreset.Minimal] = ["deposit_with_reorg", "ex_ante", "get_head", "get_proposer_head", "on_block", "reorg", "withholding"],
        [ConsensusPreset.Mainnet] = ["ex_ante", "get_head", "get_proposer_head", "on_block"],
    };

    // A wrong suite path or an emptied case source enumerates zero vectors, and zero vectors run green.
    // The fork set is not asserted: Cases reads fulu only, and GloasForkChoiceTests reads gloas.
    [Test]
    public void Every_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<ForkChoiceCase> cases = FuluDriverSupport.TestedCases<ForkChoiceCase>(preset, MinimalCases, MainnetCases);
        Assert.That(cases.Select(HandlerOf).Distinct(), Is.EquivalentTo(HandlersByPreset[preset]));
    }

    // Not-implemented vectors are Inconclusive, so a step driver that reports every mainnet vector that way still runs green.
    [Test]
    public void Every_handler_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<ForkChoiceCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases), HandlerOf, Run);

    /// <summary>The mainnet vector whose anchor and blocks <see cref="Refused_block_step_replays_its_body_only_when_refused_optimistically"/> reuses.</summary>
    private const string FabricationSource = "get_proposer_head/pyspec_tests/basic_is_parent_root";

    /// <summary>Slot 33, a child of the anchor whose body carries attestations that vote for the anchor.</summary>
    private const string AttestingBlock = "block_0x9efbe54f7970373161723e08fb309354c6cfab3cfa15b003c2f9af393f150c43";

    /// <summary>The store time, in seconds after genesis, at which slot 33 is the current slot.</summary>
    private const ulong TickAtBlockSlot = 33 * 12;

    /// <summary>
    /// pyspec's <c>add_block(valid=False)</c> replays a refused block's body only on its optimistic path, where an
    /// on_payload_info step made the execution layer answer INVALID (tests/core/pyspec/eth_consensus_specs/test/helpers/fork_choice.py).
    /// The attesting block's votes are what show a replay. Either way the refused block stays out of the block tree and its
    /// child is refused: an INVALIDATED block is removed from the block tree (specs/bellatrix/optimistic-sync.md).
    /// </summary>
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

    /// <summary>Runs <paramref name="steps"/> from the anchor of <see cref="FabricationSource"/>, with <paramref name="blocks"/> as the case's block files.</summary>
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

    /// <summary>The handler directory, the segment after <c>{preset}/fulu/fork_choice/</c> in the vector name.</summary>
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
