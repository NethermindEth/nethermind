// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Enumerates the consensus-specs <c>fork_choice</c> suite without driving it: this repo has a
/// <c>ForkChoiceRunner</c>/<c>ForkChoiceStore</c>, but wiring their on_tick/on_block/on_attestation
/// surface to the steps.yaml script format is real work this task did not reach (see the report). Every
/// vector is still enumerated and named from its manifest.yaml (kept by
/// <see cref="ConsensusSpecArchive"/>'s selective extraction precisely so this suite never has to guess
/// a count), and reported not-implemented - explicit and counted, never a silent skip and never a pass,
/// per this task's reporting rules.
/// </summary>
[TestFixture]
public class ForkChoiceTests
{
    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(ForkChoiceCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("fork_choice", "fulu", testCase.Preset, testCase.VectorName, () =>
            throw new NotImplementedInDriverException(
                "fork_choice driving is not implemented in this pass: this repo's ForkChoiceRunner/ForkChoiceStore " +
                "is not wired to the steps.yaml on_tick/on_block/on_attestation/checks script format."));

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

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
