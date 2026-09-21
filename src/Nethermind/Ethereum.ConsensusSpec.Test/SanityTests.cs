// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>sanity</c> suite: <c>blocks</c> (apply a sequence of signed blocks via
/// the fork's <c>state_transition</c>) and <c>slots</c> (advance slots via the fork's
/// <c>process_slots</c>), both through the vector's <see cref="ForkDriver"/>. Driven for the same forks
/// as <see cref="OperationsTests"/>.
/// </summary>
[TestFixture]
public class SanityTests
{
    private sealed class FixedNewPayloadNotifier(bool valid) : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => valid ? ExecutionStatus.Valid : ExecutionStatus.Invalid;
    }

    [TestCaseSource(nameof(MinimalBlockCases))]
    public void Blocks(SanityCase testCase) => ExecuteBlocks(testCase);

    [TestCaseSource(nameof(MainnetBlockCases))]
    public void Blocks_mainnet(SanityCase testCase) => ExecuteBlocks(testCase);

    [TestCaseSource(nameof(MinimalSlotCases))]
    public void Slots(SanityCase testCase) => ExecuteSlots(testCase);

    [TestCaseSource(nameof(MainnetSlotCases))]
    public void Slots_mainnet(SanityCase testCase) => ExecuteSlots(testCase);

    private static void ExecuteBlocks(SanityCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("sanity/blocks", testCase.Fork, testCase.Preset, testCase.VectorName, () =>
        {
            RequireMainnetPreset(testCase.Preset);
            ForkDriver driver = FuluDriverSupport.RequireForkDriver(testCase.Fork);
            BeaconStateFulu state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
            EpochCache cache = driver.NewCache();
            PubkeyCache pubkeys = FuluDriverSupport.BuildPubkeyCache(state);
            bool verifySignatures = FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath);

            Dictionary<string, string> meta = FuluDriverSupport.ParseFlowMap(Path.Combine(testCase.CasePath, "meta.yaml"));
            int blocksCount = int.Parse(meta["blocks_count"]);

            string postPath = Path.Combine(testCase.CasePath, "post.ssz_snappy");
            bool expectSuccess = File.Exists(postPath);

            Exception? thrown = null;
            try
            {
                for (int i = 0; i < blocksCount; i++)
                {
                    byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, $"blocks_{i}.ssz_snappy"));
                    SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock signedBlock);
                    FixedNewPayloadNotifier notifier = new(valid: true);
                    driver.ApplyBlock(state, signedBlock, cache, pubkeys, notifier, verifySignatures);
                }
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            if (expectSuccess)
            {
                if (thrown is not null)
                    Assert.Fail($"expected all {blocksCount} block(s) to apply, but it threw: {thrown}");

                FuluDriverSupport.AssertPostStateRoot(driver, postPath, state);
            }
            else
            {
                FuluDriverSupport.AssertRejected(thrown, "the block sequence");
            }
        });

    private static void ExecuteSlots(SanityCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("sanity/slots", testCase.Fork, testCase.Preset, testCase.VectorName, () =>
        {
            RequireMainnetPreset(testCase.Preset);
            ForkDriver driver = FuluDriverSupport.RequireForkDriver(testCase.Fork);
            BeaconStateFulu state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
            EpochCache cache = driver.NewCache();
            int slots = FuluDriverSupport.ParseScalarInt(Path.Combine(testCase.CasePath, "slots.yaml"));

            driver.ProcessSlots(state, state.Slot + (ulong)slots, cache);

            FuluDriverSupport.AssertPostStateRoot(driver, Path.Combine(testCase.CasePath, "post.ssz_snappy"), state);
        });

    private static void RequireMainnetPreset(string preset)
    {
        if (preset == nameof(ConsensusPreset.Minimal))
        {
            throw new NotImplementedInDriverException(
                "This repo's BeaconState containers hard-code mainnet-preset-scaled vector bounds, so they cannot decode a " +
                "minimal-preset pre.ssz_snappy at all; this suite only runs for real against the mainnet preset " +
                "(opt in with NETHERMIND_CONSENSUS_SPEC_MAINNET=1).");
        }
    }

    private static IEnumerable<TestCaseData> MinimalBlockCases() => Cases(ConsensusPreset.Minimal, "blocks", "meta.yaml");
    private static IEnumerable<TestCaseData> MinimalSlotCases() => Cases(ConsensusPreset.Minimal, "slots", "slots.yaml");

    private static IEnumerable<TestCaseData> MainnetBlockCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled) yield break;
        foreach (TestCaseData data in Cases(ConsensusPreset.Mainnet, "blocks", "meta.yaml")) yield return data;
    }

    private static IEnumerable<TestCaseData> MainnetSlotCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled) yield break;
        foreach (TestCaseData data in Cases(ConsensusPreset.Mainnet, "slots", "slots.yaml")) yield return data;
    }

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset, string subSuite, string marker)
    {
        foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
        {
            string? sanityRoot = ConsensusSpecArchive.SuitePath(preset, fork, "sanity");
            if (sanityRoot is null)
                continue;

            string subDir = Path.Combine(sanityRoot, subSuite);
            foreach (string caseDir in ConsensusSpecArchive.LeafDirs(subDir, marker))
            {
                string vectorName = $"{preset}/{fork}/sanity/{subSuite}/{Path.GetFileName(caseDir)}";
                SanityCase testCase = new(preset.ToString(), fork, caseDir, vectorName);
                yield return new TestCaseData(testCase).SetName(vectorName);
            }
        }
    }
}

public readonly record struct SanityCase(string Preset, string Fork, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
