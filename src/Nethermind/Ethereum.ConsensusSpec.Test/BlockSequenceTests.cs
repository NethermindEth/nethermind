// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>finality</c> and <c>random</c> suites, whose vectors are block sequences in the
/// <c>sanity/blocks</c> format (tests/formats/finality/README.md, tests/formats/random/README.md), through the
/// same runner as <see cref="SanityTests"/>, for every fork in <see cref="ConsensusSpecArchive.StateTransitionForks"/>.
/// </summary>
[TestFixture]
public class BlockSequenceTests
{
    /// <summary>Each suite with its only handler at <see cref="ConsensusSpecArchive.Version"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, string> HandlerBySuite = new Dictionary<string, string>
    {
        ["finality"] = "finality",
        ["random"] = "random",
    };

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(SanityCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(SanityCase testCase) => Execute(testCase);

    // A wrong suite path or an emptied case source enumerates zero vectors; a handler this driver does not enumerate never runs; both stay green.
    [Test]
    public void Every_fork_and_suite_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<SanityCase> cases = FuluDriverSupport.TestedCases<SanityCase>(preset, MinimalCases, MainnetCases);
        using (Assert.EnterMultipleScope())
        {
            foreach ((string suite, string handler) in HandlerBySuite)
            {
                Assert.That(cases.Where(testCase => SuiteOf(testCase) == suite).Select(static testCase => testCase.Fork).Distinct(),
                    Is.EquivalentTo(ConsensusSpecArchive.StateTransitionForks), suite);
                foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
                {
                    Assert.That(ConsensusSpecArchive.SubDirs(ConsensusSpecArchive.SuitePath(preset, fork, suite)).Select(Path.GetFileName),
                        Is.EquivalentTo(new[] { handler }), $"{fork} {suite} handlers in the archive");
                }
            }
        }
    }

    // Not-implemented vectors are Inconclusive, so a driver that reports every mainnet vector that way still runs green.
    [Test]
    public void Every_fork_and_suite_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<SanityCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases),
            static testCase => $"{testCase.Fork}/{SuiteOf(testCase)}",
            SanityTests.RunBlocks);

    /// <summary>The suite directory, the segment after <c>{preset}/{fork}/</c> in the vector name.</summary>
    private static string SuiteOf(SanityCase testCase) => testCase.VectorName.Split('/')[2];

    private static void Execute(SanityCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord(SuiteOf(testCase), testCase.Fork, testCase.Preset, testCase.VectorName, () => SanityTests.RunBlocks(testCase));

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
        foreach ((string suite, string handler) in HandlerBySuite)
        {
            foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
            {
                string? suiteRoot = ConsensusSpecArchive.SuitePath(preset, fork, suite);
                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(suiteRoot is null ? null : Path.Combine(suiteRoot, handler), "meta.yaml"))
                {
                    string vectorName = $"{preset}/{fork}/{suite}/{handler}/{Path.GetFileName(caseDir)}";
                    yield return new TestCaseData(new SanityCase(preset.ToString(), fork, caseDir, vectorName)).SetName(vectorName);
                }
            }
        }
    }
}
