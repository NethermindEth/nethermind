// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>fork</c> suite: decode the pre-fork <c>pre</c> state, apply the fork's
/// <c>upgrade_to_*</c>, and compare the result's <c>hash_tree_root</c> with the expected <c>post</c>
/// state. Driven for <see cref="ConsensusSpecArchive.ForkUpgradeForks"/>.
/// </summary>
[TestFixture]
public class ForkTests
{
    /// <summary>
    /// Upgrades a pre-fork state file into the named fork's state, and decodes an expected post-fork
    /// state file; each result carries its <c>hash_tree_root</c>.
    /// </summary>
    internal readonly record struct ForkUpgrade(Func<string, (object State, Hash256 Root)> UpgradePre, Func<string, (object State, Hash256 Root)> DecodePost);

    /// <summary>The upgrade per post-fork name; <see cref="ConsensusSpecArchive.ForkUpgradeForks"/> must extract exactly these keys.</summary>
    internal static readonly IReadOnlyDictionary<string, ForkUpgrade> UpgradesByFork = new Dictionary<string, ForkUpgrade>(StringComparer.Ordinal)
    {
        ["gloas"] = new(
            prePath => RoundTripped(GloasForkTransition.UpgradeToGloas(FuluDriverSupport.DecodeState(prePath), BeaconChainSpec.Mainnet)),
            postPath =>
            {
                BeaconStateGloas post = DecodeGloas(postPath);
                return (post, SszRoots.HashTreeRoot(post));
            }),
    };

    [TestCaseSource(nameof(MinimalCases))]
    public void Fork(ForkCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Fork_mainnet(ForkCase testCase) => Execute(testCase);

    // A wrong suite path, a dropped extraction entry or an emptied case source enumerates zero vectors, and zero vectors run green.
    [Test]
    public void Every_fork_upgrade_fork_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        IEnumerable<string> forksWithVectors = TestedCases(preset).Select(static testCase => testCase.Fork).Distinct();
        Assert.That(forksWithVectors, Is.EquivalentTo(ConsensusSpecArchive.ForkUpgradeForks));
    }

    // Not-implemented vectors are Inconclusive, so a driver that reports every mainnet vector that way still runs green.
    [Test]
    public void Every_fork_upgrade_fork_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented()
    {
        foreach (string fork in ConsensusSpecArchive.ForkUpgradeForks)
        {
            ForkCase? testCase = TestedCases(ConsensusPreset.Mainnet).Cast<ForkCase?>().FirstOrDefault(testCase => testCase!.Value.Fork == fork);
            Assert.That(testCase, Is.Not.Null, $"no mainnet '{fork}' fork vector is enumerated");
            Assert.That(() => Run(testCase!.Value), Throws.Nothing, testCase!.Value.VectorName);
        }
    }

    /// <summary>The cases the <see cref="Fork"/> or <see cref="Fork_mainnet"/> test consumes for <paramref name="preset"/>.</summary>
    private static List<ForkCase> TestedCases(ConsensusPreset preset) => FuluDriverSupport.TestedCases<ForkCase>(preset, MinimalCases, MainnetCases);

    private static void Execute(ForkCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("fork", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(ForkCase testCase)
    {
        FuluDriverSupport.RequireMainnetPreset(testCase.Preset);

        if (!UpgradesByFork.TryGetValue(testCase.Fork, out ForkUpgrade upgrade))
            throw new NotImplementedInDriverException($"fork '{testCase.Fork}' has no upgrade in this driver.");

        (object actual, Hash256 actualRoot) = upgrade.UpgradePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        (object expected, Hash256 expectedRoot) = upgrade.DecodePost(Path.Combine(testCase.CasePath, "post.ssz_snappy"));

        if (expectedRoot != actualRoot)
        {
            List<string> diff = FuluDriverSupport.Diff(expected, actual, "state");
            Assert.Fail($"post-state root mismatch: expected {expectedRoot}, actual {actualRoot}. Diverging fields: {(diff.Count > 0 ? string.Join("; ", diff) : "(none found - roots differ anyway)")}");
        }
    }

    /// <summary>The state's SSZ round-trip, so the diff compares serialized values rather than a null against its zero default, with the state's own root.</summary>
    private static (object State, Hash256 Root) RoundTripped(BeaconStateGloas state)
    {
        BeaconStateGloas.Decode(BeaconStateGloas.Encode(state), out BeaconStateGloas roundTripped);
        return (roundTripped, SszRoots.HashTreeRoot(state));
    }

    private static BeaconStateGloas DecodeGloas(string path)
    {
        BeaconStateGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(path), out BeaconStateGloas state);
        return state;
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled) yield break;
        foreach (TestCaseData data in Cases(ConsensusPreset.Mainnet)) yield return data;
    }

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        foreach (string fork in ConsensusSpecArchive.ForkUpgradeForks)
        {
            string? forkRoot = ConsensusSpecArchive.SuitePath(preset, fork, "fork");
            foreach (string caseDir in ConsensusSpecArchive.LeafDirs(forkRoot, "pre.ssz_snappy"))
            {
                string vectorName = $"{preset}/{fork}/fork/{Path.GetRelativePath(forkRoot!, caseDir).Replace('\\', '/')}";
                ForkCase testCase = new(preset.ToString(), fork, caseDir, vectorName);
                yield return new TestCaseData(testCase).SetName(vectorName);
            }
        }
    }
}

public readonly record struct ForkCase(string Preset, string Fork, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
