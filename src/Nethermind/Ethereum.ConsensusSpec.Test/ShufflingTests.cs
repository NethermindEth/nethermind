// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.Core.Extensions;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <remarks>Phase0 vectors cover unchanged later-fork shuffling; preset SHUFFLE_ROUND_COUNT is passed explicitly.</remarks>
[TestFixture]
public class ShufflingTests
{
    private static readonly IReadOnlyDictionary<ConsensusPreset, int> RoundsByPreset = new Dictionary<ConsensusPreset, int>
    {
        [ConsensusPreset.Minimal] = 10,
        [ConsensusPreset.Mainnet] = 90,
    };

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(ShufflingCase testCase) => Execute(testCase);
    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(ShufflingCase testCase) => Execute(testCase);
    [Test]
    public void Every_preset_has_vectors_in_the_archive([Values] ConsensusPreset preset) =>
        Assert.That(FuluDriverSupport.TestedCases<ShufflingCase>(preset, MinimalCases, MainnetCases), Is.Not.Empty);

    private static void Execute(ShufflingCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("shuffling", "phase0", testCase.Preset.ToString(), testCase.VectorName, () => Run(testCase));

    private static void Run(ShufflingCase testCase)
    {
        YamlMappingNode mapping = FuluDriverSupport.LoadMapping(Path.Combine(testCase.CasePath, "mapping.yaml"));
        byte[] seed = Bytes.FromHexString(FuluDriverSupport.Scalar(mapping, "seed"));
        int count = int.Parse(FuluDriverSupport.Scalar(mapping, "count"));
        int[] expected = [.. ((YamlSequenceNode)mapping.Children[new YamlScalarNode("mapping")]).Children.Select(static node => int.Parse(((YamlScalarNode)node).Value!))];
        int rounds = RoundsByPreset[testCase.Preset];

        int[] perIndex = [.. Enumerable.Range(0, count).Select(i => SwapOrNotShuffle.ComputeShuffledIndex(i, count, seed, rounds))];
        int[] wholeList = [.. Enumerable.Range(0, count)];
        SwapOrNotShuffle.ShuffleList(wholeList, seed, forwards: false, rounds);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(expected, Has.Length.EqualTo(count), "the vector's mapping covers its whole list");
        Assert.That(perIndex, Is.EqualTo(expected), "compute_shuffled_index");
        Assert.That(wholeList, Is.EqualTo(expected), "the whole-list shuffle of the identity");
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.RelativeCases(preset, ["phase0"], "shuffling", "mapping.yaml",
            static (p, fork, path, name) => new ShufflingCase(p, path, name));
}

public readonly record struct ShufflingCase(ConsensusPreset Preset, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
