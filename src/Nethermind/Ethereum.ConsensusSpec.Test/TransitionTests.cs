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
    [Test]
    public void Every_transition_fork_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        IEnumerable<string> forksWithVectors = TestedCases(preset).Select(static testCase => testCase.Fork).Distinct();
        Assert.That(forksWithVectors, Is.EquivalentTo(ConsensusSpecArchive.TransitionForks));
    }
    [Test]
    public void Every_transition_fork_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(TestedCases(ConsensusPreset.Mainnet), static testCase => testCase.Fork, Run);

    private static List<TransitionCase> TestedCases(ConsensusPreset preset) =>
        FuluDriverSupport.TestedCases<TransitionCase>(preset, MinimalCases, MainnetCases);

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
        EpochCache cache = new() { Hasher = new DifferentialBeaconStateHasher() };
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

        FuluDriverSupport.AssertPostStateRoot((ForkDriver<BeaconStateGloas>)FuluDriverSupport.RequireForkDriver("gloas"), postPath, gloas, cache);
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

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.RelativeCases(preset, ConsensusSpecArchive.TransitionForks, "transition", "meta.yaml",
            static (p, fork, path, name) => new TransitionCase(p.ToString(), fork, path, name));
}

public readonly record struct TransitionCase(string Preset, string Fork, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
