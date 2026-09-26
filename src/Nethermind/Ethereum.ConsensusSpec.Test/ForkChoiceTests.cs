// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

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
    // The fork set is not asserted: Cases reads fulu only, the one fork ConsensusSpecArchive extracts fork_choice for.
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

    private static IEnumerable<TestCaseData> MainnetCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled)
            yield break;
        foreach (TestCaseData data in Cases(ConsensusPreset.Mainnet))
            yield return data;
    }

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        string? forkChoiceRoot = ConsensusSpecArchive.SuitePath(preset, "fulu", "fork_choice");
        if (forkChoiceRoot is null)
            yield break;

        foreach (string caseDir in ConsensusSpecArchive.LeafDirs(forkChoiceRoot, "manifest.yaml"))
        {
            string vectorName = $"{preset}/fulu/fork_choice/{Path.GetRelativePath(forkChoiceRoot, caseDir).Replace('\\', '/')}";
            ForkChoiceCase testCase = new(preset.ToString(), caseDir, vectorName);
            yield return new TestCaseData(testCase).SetName(vectorName);
        }
    }
}

public readonly record struct ForkChoiceCase(string Preset, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
