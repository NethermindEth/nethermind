// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Enumerates the consensus-specs <c>genesis</c> vectors (<c>initialize_beacon_state_from_eth1</c> and
/// <c>is_valid_genesis_state</c>) and reports each as not-implemented, since no fork that ships them can be driven.
/// </summary>
/// <remarks>
/// At <see cref="ConsensusSpecArchive.Version"/> only the phase0 fork ships genesis vectors, and only for the minimal preset. Their
/// <c>state</c> and <c>genesis</c> files are phase0 <c>BeaconState</c> objects, which this repo has no container for, and the
/// minimal preset's vector sizes differ from the mainnet-shaped containers it has. Nor does the production code implement
/// <c>initialize_beacon_state_from_eth1</c> or <c>is_valid_genesis_state</c>, which a driver would call. A fork whose state the driver can decode must
/// not gain vectors unnoticed, so <see cref="Genesis_vectors_exist_only_for_phase0_minimal"/> fails when one does.
/// </remarks>
[TestFixture]
public class GenesisTests
{
    private const string Suite = "genesis";
    private static readonly string[] Handlers = ["initialization", "validity"];

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(GenesisCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(GenesisCase testCase) => Execute(testCase);

    // A wrong suite path or a dropped extraction entry enumerates zero vectors, and zero vectors run green.
    [Test]
    public void Genesis_vectors_exist_only_for_phase0_minimal([Values] ConsensusPreset preset)
    {
        List<GenesisCase> cases = FuluDriverSupport.TestedCases<GenesisCase>(preset, MinimalCases, MainnetCases);
        if (preset == ConsensusPreset.Mainnet)
        {
            Assert.That(cases, Is.Empty, "a fork now ships mainnet genesis vectors and needs a driver");
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cases.Select(static c => c.Handler).Distinct(), Is.EquivalentTo(Handlers));
            Assert.That(ConsensusSpecArchive.SubDirs(ConsensusSpecArchive.SuitePath(preset, "phase0", Suite)).Select(Path.GetFileName), Is.EquivalentTo(Handlers), "handlers in the archive");
            Assert.That(cases.Select(static c => c.Fork).Distinct(), Is.EquivalentTo(new[] { "phase0" }));
            // A driver that passes without checking a state would turn every vector green unseen.
            Assert.That(cases.Where(static c => !ThrowsNotImplemented(c)), Is.Empty, "vectors that ran without reporting not-implemented");
        }
    }

    private static bool ThrowsNotImplemented(GenesisCase testCase)
    {
        try
        {
            Run(testCase);
            return false;
        }
        catch (NotImplementedInDriverException)
        {
            return true;
        }
    }

    private static void Execute(GenesisCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord(Suite, testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(GenesisCase testCase) =>
        throw new NotImplementedInDriverException(
            $"the {testCase.Fork} genesis state is a {testCase.Fork} BeaconState of the {testCase.Preset} preset, and this repo models no such container");

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() => ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        string presetRoot = Path.Combine(ConsensusSpecArchive.GetRoot(preset), "tests", ConsensusSpecArchive.PresetDirName(preset));
        foreach (string forkDir in Directory.Exists(presetRoot) ? Directory.GetDirectories(presetRoot).Order(StringComparer.Ordinal).ToArray() : Array.Empty<string>())
        {
            string fork = Path.GetFileName(forkDir);
            foreach (string handler in Handlers)
            {
                string handlerRoot = Path.Combine(forkDir, Suite, handler);
                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(handlerRoot, "manifest.yaml"))
                {
                    string vectorName = $"{preset}/{fork}/{Suite}/{handler}/{Path.GetRelativePath(handlerRoot, caseDir).Replace('\\', '/')}";
                    yield return new TestCaseData(new GenesisCase(preset.ToString(), fork, handler, vectorName)).SetName(vectorName);
                }
            }
        }
    }
}

public readonly record struct GenesisCase(string Preset, string Fork, string Handler, string VectorName)
{
    public override string ToString() => VectorName;
}
