// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>sanity</c> suite: <c>blocks</c> (apply a sequence of signed blocks via
/// <see cref="StateTransition"/>) and <c>slots</c> (advance slots via <see cref="SlotProcessing"/>).
/// Fulu-only, for the same reason as <see cref="OperationsTests"/>.
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
        ConsensusSpecTestSummary.RunAndRecord("sanity/blocks", "fulu", testCase.Preset, testCase.VectorName, () =>
        {
            RequireMainnetPreset(testCase.Preset);
            BeaconStateFulu state = FuluDriverSupport.DecodeState(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
            EpochCache cache = new();
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
                    StateTransition.Apply(state, signedBlock, cache, pubkeys, notifier, BeaconChainSpec.Mainnet, validateResult: true, verifySignatures);
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

                BeaconStateFulu expectedPost = FuluDriverSupport.DecodeState(postPath);
                Hash256 expectedRoot = FuluDriverSupport.StateRoot(expectedPost);
                Hash256 actualRoot = FuluDriverSupport.StateRoot(state);
                if (expectedRoot != actualRoot)
                {
                    List<string> diff = FuluDriverSupport.Diff(expectedPost, state, "state");
                    Assert.Fail($"post-state root mismatch: expected {expectedRoot}, actual {actualRoot}. Diverging fields: {(diff.Count > 0 ? string.Join("; ", diff) : "(none found - roots differ anyway)")}");
                }
            }
            else if (thrown is null)
            {
                Assert.Fail("expected block application to fail at some point, but the whole sequence completed without error");
            }
        });

    private static void ExecuteSlots(SanityCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("sanity/slots", "fulu", testCase.Preset, testCase.VectorName, () =>
        {
            RequireMainnetPreset(testCase.Preset);
            BeaconStateFulu state = FuluDriverSupport.DecodeState(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
            EpochCache cache = new();
            int slots = FuluDriverSupport.ParseScalarInt(Path.Combine(testCase.CasePath, "slots.yaml"));

            SlotProcessing.ProcessSlots(state, state.Slot + (ulong)slots, cache);

            BeaconStateFulu expectedPost = FuluDriverSupport.DecodeState(Path.Combine(testCase.CasePath, "post.ssz_snappy"));
            Hash256 expectedRoot = FuluDriverSupport.StateRoot(expectedPost);
            Hash256 actualRoot = FuluDriverSupport.StateRoot(state);
            if (expectedRoot != actualRoot)
            {
                List<string> diff = FuluDriverSupport.Diff(expectedPost, state, "state");
                Assert.Fail($"post-state root mismatch: expected {expectedRoot}, actual {actualRoot}. Diverging fields: {(diff.Count > 0 ? string.Join("; ", diff) : "(none found - roots differ anyway)")}");
            }
        });

    private static void RequireMainnetPreset(string preset)
    {
        if (preset == nameof(ConsensusPreset.Minimal))
        {
            throw new NotImplementedInDriverException(
                "BeaconStateFulu's SSZ shape hard-codes mainnet-preset-scaled vector bounds, so it cannot decode a " +
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
        string? sanityRoot = ConsensusSpecArchive.SuitePath(preset, "fulu", "sanity");
        if (sanityRoot is null)
            yield break;

        string subDir = Path.Combine(sanityRoot, subSuite);
        foreach (string caseDir in ConsensusSpecArchive.LeafDirs(subDir, marker))
        {
            string vectorName = $"{preset}/fulu/sanity/{subSuite}/{Path.GetFileName(caseDir)}";
            SanityCase testCase = new(preset.ToString(), caseDir, vectorName);
            yield return new TestCaseData(testCase).SetName(vectorName);
        }
    }
}

public readonly record struct SanityCase(string Preset, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
