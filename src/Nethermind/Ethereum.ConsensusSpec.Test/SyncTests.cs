// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>sync</c> suite, which reuses the fork_choice format with on_payload_info steps
/// (tests/formats/sync/README.md), through <see cref="ForkChoiceStepDriver"/>. Fulu only, and mainnet-preset only for
/// the same reason as <see cref="ForkChoiceTests"/>.
/// </summary>
[TestFixture]
public class SyncTests
{
    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(ForkChoiceCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(ForkChoiceCase testCase) => Execute(testCase);

    // A wrong suite path or an emptied case source enumerates zero vectors, and zero vectors run green.
    [Test]
    public void Every_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset) =>
        Assert.That(FuluDriverSupport.TestedCases<ForkChoiceCase>(preset, MinimalCases, MainnetCases).Select(static testCase => testCase.VectorName.Split('/')[3]).Distinct(),
            Is.EquivalentTo(new[] { "optimistic" }));

    // Not-implemented vectors are Inconclusive, so a driver that reports every mainnet vector that way still runs green.
    [Test]
    public void Every_handler_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<ForkChoiceCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases), static testCase => testCase.VectorName.Split('/')[3], Run);

    private static void Execute(ForkChoiceCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("sync", "fulu", testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(ForkChoiceCase testCase)
    {
        FuluDriverSupport.RequireMainnetPreset(testCase.Preset);
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
        string? syncRoot = ConsensusSpecArchive.SuitePath(preset, "fulu", "sync");
        foreach (string caseDir in ConsensusSpecArchive.LeafDirs(syncRoot, "steps.yaml"))
        {
            string vectorName = $"{preset}/fulu/sync/{Path.GetRelativePath(syncRoot!, caseDir).Replace('\\', '/')}";
            yield return new TestCaseData(new ForkChoiceCase(preset.ToString(), caseDir, vectorName)).SetName(vectorName);
        }
    }
}
