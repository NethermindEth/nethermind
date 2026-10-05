// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>Reports genesis vectors as not-implemented: only phase0/minimal fixtures exist, with no supported state container or genesis operations.</summary>
[TestFixture]
public class GenesisTests
{
    private const string Suite = "genesis";
    private static readonly string[] Handlers = ["initialization", "validity"];

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(GenesisCase testCase) => Execute(testCase);
    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(GenesisCase testCase) => Execute(testCase);
    [Test]
    public void Genesis_vectors_exist_only_for_phase0_minimal([Values] ConsensusPreset preset)
    {
        List<GenesisCase> cases = FuluDriverSupport.TestedCases<GenesisCase>(preset, MinimalCases, MainnetCases);
        if (preset == ConsensusPreset.Mainnet)
        {
            Assert.That(cases, Is.Empty, "a fork now ships mainnet genesis vectors and needs a driver");
            return;
        }

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(cases.Select(static c => c.Handler).Distinct(), Is.EquivalentTo(Handlers));
        Assert.That(ConsensusSpecArchive.SubDirs(ConsensusSpecArchive.SuitePath(preset, "phase0", Suite)).Select(Path.GetFileName), Is.EquivalentTo(Handlers), "handlers in the archive");
        Assert.That(cases.Select(static c => c.Fork).Distinct(), Is.EquivalentTo(new[] { "phase0" }));
        // A driver that passes without checking a state would turn every vector green unseen.
        Assert.That(cases.Where(static c => !ThrowsNotImplemented(c)), Is.Empty, "vectors that ran without reporting not-implemented");
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
