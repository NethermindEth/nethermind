// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>transition</c> suite: a pre-fork <c>pre</c> state and a block sequence that
/// crosses meta.yaml's <c>fork_epoch</c>, every block applied through
/// <see cref="ForkedStateTransition.Apply"/>, compared by the post-fork <c>post</c> state's
/// <c>hash_tree_root</c>. Driven for <see cref="ConsensusSpecArchive.TransitionForks"/>.
/// </summary>
[TestFixture]
public class TransitionTests
{
    private sealed class ValidPayloadNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }

    [TestCaseSource(nameof(MinimalCases))]
    public void Transition(TransitionCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Transition_mainnet(TransitionCase testCase) => Execute(testCase);

    // A wrong suite path, a dropped extraction entry or an emptied case source enumerates zero vectors, and zero vectors run green.
    [Test]
    public void Every_transition_fork_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        if (preset == ConsensusPreset.Mainnet && !ConsensusSpecArchive.MainnetEnabled)
            Assert.Ignore("mainnet vectors are opt-in (NETHERMIND_CONSENSUS_SPEC_MAINNET=1)");

        IEnumerable<string> forksWithVectors = (preset == ConsensusPreset.Mainnet ? MainnetCases() : MinimalCases())
            .Select(static data => ((TransitionCase)data.Arguments[0]!).Fork)
            .Distinct();
        Assert.That(forksWithVectors, Is.EquivalentTo(ConsensusSpecArchive.TransitionForks));
    }

    private static void Execute(TransitionCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("transition", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(TransitionCase testCase)
    {
        FuluDriverSupport.RequireMainnetPreset(testCase.Preset);

        Dictionary<string, string> meta = FuluDriverSupport.ParseFlowMap(Path.Combine(testCase.CasePath, "meta.yaml"));
        if (meta["post_fork"] != "gloas")
            throw new NotImplementedInDriverException($"post_fork '{meta["post_fork"]}' has no boundary crossing in ForkedStateTransition.");

        int blocksCount = int.Parse(meta["blocks_count"]);
        // fork_block is the index of the last pre-fork block; absent means every block is post-fork.
        int lastPreForkBlock = meta.TryGetValue("fork_block", out string? forkBlock) ? int.Parse(forkBlock) : -1;
        BeaconChainSpec spec = FuluDriverSupport.TransitionSpec(ulong.Parse(meta["fork_epoch"]));
        bool verifySignatures = FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath);

        BeaconStateFulu pre = FuluDriverSupport.DecodeState(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        ForkedBeaconState state = new ForkedBeaconState.OfFulu(pre);
        EpochCache cache = new();
        PubkeyCache pubkeys = FuluDriverSupport.BuildPubkeyCache(pre.Validators!);
        ValidPayloadNotifier notifier = new();

        string postPath = Path.Combine(testCase.CasePath, "post.ssz_snappy");
        bool expectSuccess = File.Exists(postPath);

        for (int i = 0; i < blocksCount; i++)
        {
            byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, $"blocks_{i}.ssz_snappy"));
            ForkedSignedBeaconBlock block = i <= lastPreForkBlock ? DecodeFulu(ssz) : DecodeGloas(ssz);

            // Only the last block of an invalid vector may be rejected; every earlier one is part of the valid prefix.
            if (!expectSuccess && i == blocksCount - 1)
            {
                Exception? thrown = null;
                try
                {
                    ForkedStateTransition.Apply(state, block, cache, pubkeys, notifier, spec, verifySignatures: verifySignatures);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                FuluDriverSupport.AssertRejected(thrown, $"block {i}");
                return;
            }

            try
            {
                state = ForkedStateTransition.Apply(state, block, cache, pubkeys, notifier, spec, verifySignatures: verifySignatures);
            }
            catch (Exception ex)
            {
                Assert.Fail($"expected block {i} of {blocksCount} to apply, but it threw: {ex}");
            }
        }

        if (!expectSuccess)
            Assert.Fail("the vector has no post state, so its last block must be rejected, but it has no blocks");

        if (state is not ForkedBeaconState.OfGloas { State: BeaconStateGloas gloas })
        {
            Assert.Fail($"expected the state to have crossed into Gloas, but it is still {state.Fork}");
            return;
        }

        FuluDriverSupport.AssertPostStateRoot((ForkDriver<BeaconStateGloas>)FuluDriverSupport.RequireForkDriver("gloas"), postPath, gloas);
    }

    private static ForkedSignedBeaconBlock DecodeFulu(byte[] ssz)
    {
        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock block);
        return new ForkedSignedBeaconBlock.OfFulu(block);
    }

    private static ForkedSignedBeaconBlock DecodeGloas(byte[] ssz)
    {
        SignedBeaconBlockGloas.Decode(ssz, out SignedBeaconBlockGloas block);
        return new ForkedSignedBeaconBlock.OfGloas(block);
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled) yield break;
        foreach (TestCaseData data in Cases(ConsensusPreset.Mainnet)) yield return data;
    }

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        foreach (string fork in ConsensusSpecArchive.TransitionForks)
        {
            string? transitionRoot = ConsensusSpecArchive.SuitePath(preset, fork, "transition");
            foreach (string caseDir in ConsensusSpecArchive.LeafDirs(transitionRoot, "meta.yaml"))
            {
                string vectorName = $"{preset}/{fork}/transition/{Path.GetRelativePath(transitionRoot!, caseDir).Replace('\\', '/')}";
                TransitionCase testCase = new(preset.ToString(), fork, caseDir, vectorName);
                yield return new TestCaseData(testCase).SetName(vectorName);
            }
        }
    }
}

public readonly record struct TransitionCase(string Preset, string Fork, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
