// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class SanityTests
{
    [TestCaseSource(nameof(MinimalBlockCases))]
    public void Blocks(SanityCase testCase) => ExecuteBlocks(testCase);
    [TestCaseSource(nameof(MainnetBlockCases))]
    public void Blocks_mainnet(SanityCase testCase) => ExecuteBlocks(testCase);
    [TestCaseSource(nameof(MinimalSlotCases))]
    public void Slots(SanityCase testCase) => ExecuteSlots(testCase);
    [TestCaseSource(nameof(MainnetSlotCases))]
    public void Slots_mainnet(SanityCase testCase) => ExecuteSlots(testCase);
    [Test]
    public void Every_fork_and_sub_suite_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<SanityCase> blocks = FuluDriverSupport.TestedCases<SanityCase>(preset, MinimalBlockCases, MainnetBlockCases);
        List<SanityCase> slots = FuluDriverSupport.TestedCases<SanityCase>(preset, MinimalSlotCases, MainnetSlotCases);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(blocks.Select(static testCase => testCase.Fork).Distinct(), Is.EquivalentTo(ConsensusSpecArchive.StateTransitionForks), "blocks");
        Assert.That(slots.Select(static testCase => testCase.Fork).Distinct(), Is.EquivalentTo(ConsensusSpecArchive.StateTransitionForks), "slots");
        foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
        {
            IEnumerable<string> subSuites = ConsensusSpecArchive.SubDirs(ConsensusSpecArchive.SuitePath(preset, fork, "sanity")).Select(Path.GetFileName)!;
            Assert.That(subSuites, Is.EquivalentTo(SubSuites), $"{fork} sanity sub-suites in the archive");
        }
    }
    [Test]
    public void Every_fork_and_sub_suite_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented()
    {
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<SanityCase>(ConsensusPreset.Mainnet, MinimalBlockCases, MainnetBlockCases), static testCase => testCase.Fork, RunBlocks);
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<SanityCase>(ConsensusPreset.Mainnet, MinimalSlotCases, MainnetSlotCases), static testCase => testCase.Fork, RunSlots);
    }

    private static void ExecuteBlocks(SanityCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("sanity/blocks", testCase.Fork, testCase.Preset, testCase.VectorName, () => RunBlocks(testCase));

    internal static void RunBlocks(SanityCase testCase)
    {
        FuluDriverSupport.RequireMainnetPreset(testCase.Preset);
        switch (FuluDriverSupport.RequireForkDriver(testCase.Fork))
        {
            case ForkDriver<BeaconStateFulu> fulu:
                RunBlocks(testCase, fulu);
                break;
            case ForkDriver<BeaconStateGloas> gloas:
                RunBlocks(testCase, gloas);
                break;
            case ForkDriver other:
                throw new NotImplementedInDriverException($"fork '{other.Fork}' has no state type this suite knows.");
        }
    }

    private static void RunBlocks<TState>(SanityCase testCase, ForkDriver<TState> driver) where TState : class
    {
        TState state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        EpochCache cache = driver.NewCache();
        PubkeyCache pubkeys = FuluDriverSupport.BuildPubkeyCache(driver.ValidatorsOf(state));
        bool verifySignatures = FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath);
        BeaconChainSpec spec = FuluDriverSupport.CaseSpec(testCase.CasePath);

        Dictionary<string, string> meta = FuluDriverSupport.ParseFlowMap(Path.Combine(testCase.CasePath, "meta.yaml"));
        int blocksCount = int.Parse(meta["blocks_count"]);

        string postPath = Path.Combine(testCase.CasePath, "post.ssz_snappy");
        FuluDriverSupport.AssertTransition(driver, postPath, state, cache, () =>
        {
            for (int i = 0; i < blocksCount; i++)
            {
                byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, $"blocks_{i}.ssz_snappy"));
                ForkChoiceStepDriver.FixedNewPayloadNotifier notifier = new(ExecutionStatus.Valid);
                driver.ApplyBlock(state, ssz, spec, cache, pubkeys, notifier, verifySignatures);
            }
        }, "the block sequence", $"expected all {blocksCount} block(s) to apply, but it threw");
    }

    private static void ExecuteSlots(SanityCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("sanity/slots", testCase.Fork, testCase.Preset, testCase.VectorName, () => RunSlots(testCase));

    private static void RunSlots(SanityCase testCase)
    {
        FuluDriverSupport.RequireMainnetPreset(testCase.Preset);
        switch (FuluDriverSupport.RequireForkDriver(testCase.Fork))
        {
            case ForkDriver<BeaconStateFulu> fulu:
                RunSlots(testCase, fulu);
                break;
            case ForkDriver<BeaconStateGloas> gloas:
                RunSlots(testCase, gloas);
                break;
            case ForkDriver other:
                throw new NotImplementedInDriverException($"fork '{other.Fork}' has no state type this suite knows.");
        }
    }

    private static void RunSlots<TState>(SanityCase testCase, ForkDriver<TState> driver) where TState : class
    {
        TState state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        EpochCache cache = driver.NewCache();
        int slots = FuluDriverSupport.ParseScalarInt(Path.Combine(testCase.CasePath, "slots.yaml"));

        driver.ProcessSlots(state, driver.SlotOf(state) + (ulong)slots, cache);

        FuluDriverSupport.AssertPostStateRoot(driver, Path.Combine(testCase.CasePath, "post.ssz_snappy"), state, cache);
    }

    private const string BlocksSubSuite = "blocks";
    private const string SlotsSubSuite = "slots";
    private static readonly string[] SubSuites = [BlocksSubSuite, SlotsSubSuite];

    private static IEnumerable<TestCaseData> MinimalBlockCases() => Cases(ConsensusPreset.Minimal, BlocksSubSuite, "meta.yaml");
    private static IEnumerable<TestCaseData> MinimalSlotCases() => Cases(ConsensusPreset.Minimal, SlotsSubSuite, "slots.yaml");

    private static IEnumerable<TestCaseData> MainnetBlockCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet, BlocksSubSuite, "meta.yaml") : [];

    private static IEnumerable<TestCaseData> MainnetSlotCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet, SlotsSubSuite, "slots.yaml") : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset, string subSuite, string marker) =>
        FuluDriverSupport.HandlerCases(preset, ConsensusSpecArchive.StateTransitionForks, "sanity", marker,
            static (p, fork, _, path, name) => new SanityCase(p.ToString(), fork, path, name), [subSuite]);
}

public readonly record struct SanityCase(string Preset, string Fork, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
