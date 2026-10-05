// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Ethereum.ConsensusSpec.Test;

/// <remarks>Mainnet only; minimal state decoding is unsupported as in <see cref="ForkChoiceTests"/>.</remarks>
[TestFixture]
public class SyncTests
{
    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(ForkChoiceCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(ForkChoiceCase testCase) => Execute(testCase);
    [Test]
    public void Every_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset) =>
        Assert.That(FuluDriverSupport.TestedCases<ForkChoiceCase>(preset, MinimalCases, MainnetCases).Select(static testCase => testCase.VectorName.Split('/')[3]).Distinct(),
            Is.EquivalentTo(new[] { "optimistic" }));
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

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.RelativeCases(preset, ["fulu"], "sync", "steps.yaml",
            static (p, fork, path, name) => new ForkChoiceCase(p.ToString(), path, name));
}
