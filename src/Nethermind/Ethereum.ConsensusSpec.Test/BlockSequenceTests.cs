// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class BlockSequenceTests
{
    internal static readonly IReadOnlyDictionary<string, string> HandlerBySuite = new Dictionary<string, string>
    {
        ["finality"] = "finality",
        ["random"] = "random",
    };

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(SanityCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(SanityCase testCase) => Execute(testCase);
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
    [Test]
    public void Every_fork_and_suite_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<SanityCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases),
            static testCase => $"{testCase.Fork}/{SuiteOf(testCase)}",
            SanityTests.RunBlocks);

    private static string SuiteOf(SanityCase testCase) => testCase.VectorName.Split('/')[2];

    private static void Execute(SanityCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord(SuiteOf(testCase), testCase.Fork, testCase.Preset, testCase.VectorName, () => SanityTests.RunBlocks(testCase));

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        foreach ((string suite, string handler) in HandlerBySuite)
            foreach (TestCaseData data in FuluDriverSupport.HandlerCases(preset, ConsensusSpecArchive.StateTransitionForks, suite, "meta.yaml",
                static (p, fork, _, path, name) => new SanityCase(p.ToString(), fork, path, name), [handler]))
                yield return data;
    }
}
