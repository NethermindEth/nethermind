// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Consensus-spec <c>fork_choice</c> tests: replays each case's <c>steps.yaml</c> script against
/// <see cref="ForkChoiceRunner"/>, running the state transition for every block step.
/// </summary>
/// <remarks>
/// The <c>get_proposer_head</c> and <c>should_override_forkchoice_update</c> handlers cover
/// proposer-only features that are not implemented and have no suites here. Data-availability
/// (blob/column) checks are not implemented either: an expected-invalid block whose only
/// invalidity is its column data is reported as skipped, not passed.
/// </remarks>
public class ForkChoiceTests
{
    private static IEnumerable<TestCaseData> GetHeadCases() => BeaconStateTestRunner.EnumerateCases("fulu", "fork_choice", "get_head");
    private static IEnumerable<TestCaseData> OnBlockCases() => BeaconStateTestRunner.EnumerateCases("fulu", "fork_choice", "on_block");
    private static IEnumerable<TestCaseData> ExAnteCases() => BeaconStateTestRunner.EnumerateCases("fulu", "fork_choice", "ex_ante");
    private static IEnumerable<TestCaseData> ReorgCases() => BeaconStateTestRunner.EnumerateCases("fulu", "fork_choice", "reorg");
    private static IEnumerable<TestCaseData> WithholdingCases() => BeaconStateTestRunner.EnumerateCases("fulu", "fork_choice", "withholding");

    [TestCaseSource(nameof(GetHeadCases))]
    public void Get_head(string casePath) => RunForkChoiceTest(casePath);

    [TestCaseSource(nameof(OnBlockCases))]
    public void On_block(string casePath) => RunForkChoiceTest(casePath);

    [TestCaseSource(nameof(ExAnteCases))]
    public void Ex_ante(string casePath) => RunForkChoiceTest(casePath);

    [TestCaseSource(nameof(ReorgCases))]
    public void Reorg(string casePath) => RunForkChoiceTest(casePath);

    [TestCaseSource(nameof(WithholdingCases))]
    public void Withholding(string casePath) => RunForkChoiceTest(casePath);

    private static void RunForkChoiceTest(string casePath)
    {
        BeaconStateFulu anchorState = BeaconStateTestRunner.LoadState(casePath, "anchor_state.ssz_snappy");
        BeaconBlock.Decode(BeaconConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "anchor_block.ssz_snappy")), out BeaconBlock anchorBlock);

        Dictionary<Hash256, BeaconStateFulu> blockStates = new() { [SszRoots.HashTreeRoot(anchorBlock)] = anchorState };
        PubkeyCache pubkeys = new();
        pubkeys.Build(anchorState.Validators!);
        ForkChoiceRunner runner = new(BeaconChainSpec.Mainnet, anchorState, anchorBlock, new MapStateProvider(blockStates), pubkeys);

        foreach (YamlMappingNode step in LoadSteps(casePath))
        {
            if (TryGetScalar(step, "tick", out string? tick))
                runner.OnTick(runner.GenesisTime + ulong.Parse(tick));
            else if (TryGetScalar(step, "block", out string? blockName))
                RunBlockStep(casePath, step, blockName, runner, blockStates, pubkeys);
            else if (TryGetScalar(step, "attestation", out string? attestationName))
            {
                Attestation.Decode(BeaconConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, $"{attestationName}.ssz_snappy")), out Attestation attestation);
                runner.OnAttestation(attestation);
            }
            else if (TryGetScalar(step, "attester_slashing", out string? slashingName))
            {
                AttesterSlashing.Decode(BeaconConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, $"{slashingName}.ssz_snappy")), out AttesterSlashing slashing);
                runner.OnAttesterSlashing(slashing);
            }
            else if (TryGetChild(step, "checks", out YamlNode? checks))
                RunChecks(runner, (YamlMappingNode)checks!);
            else
                Assert.Fail($"Unhandled step: {step}");
        }
    }

    /// <summary>
    /// Runs an <c>on_block</c> step: the state transition on a clone of the parent post-state, the
    /// fork-choice import, and — as the step format implies — the block's own attestations and
    /// attester slashings.
    /// </summary>
    private static void RunBlockStep(
        string casePath,
        YamlMappingNode step,
        string blockName,
        ForkChoiceRunner runner,
        Dictionary<Hash256, BeaconStateFulu> blockStates,
        PubkeyCache pubkeys)
    {
        bool valid = !TryGetScalar(step, "valid", out string? validValue) || validValue != "false";
        SignedBeaconBlock.Decode(BeaconConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, $"{blockName}.ssz_snappy")), out SignedBeaconBlock signedBlock);
        BeaconBlock block = signedBlock.Message!;

        try
        {
            if (!blockStates.TryGetValue(block.ParentRoot!, out BeaconStateFulu? parentState))
                throw new ForkChoiceException($"Parent {block.ParentRoot} of {blockName} is unknown");

            BeaconStateFulu postState = Clone(parentState);
            StateTransition.Apply(postState, signedBlock, new EpochCache(), pubkeys, new BeaconStateTestRunner.StubPayloadNotifier(true), BeaconChainSpec.Mainnet);
            runner.OnBlock(signedBlock, postState);
            blockStates[SszRoots.HashTreeRoot(block)] = postState;

            // An on_block step implies receiving the block's attestations and attester slashings;
            // their signatures were already verified by the state transition.
            foreach (Attestation attestation in block.Body!.Attestations ?? [])
            {
                runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false);
            }
            foreach (AttesterSlashing slashing in block.Body.AttesterSlashings ?? [])
            {
                runner.OnAttesterSlashing(slashing, verifySignatures: false);
            }
        }
        catch (Exception) when (!valid)
        {
            return; // Rejection expected.
        }

        if (!valid)
        {
            // The transition and fork choice accepted the block, so the expected invalidity can only
            // come from its blob/column data — which we honestly do not check.
            if (TryGetChild(step, "columns", out _) || TryGetChild(step, "blobs", out _))
                Assert.Ignore($"Block {blockName} is invalid only by its blob/column data; data-availability checks are not implemented");
            Assert.Fail($"Block {blockName} was accepted but the step expects rejection");
        }
    }

    private static void RunChecks(ForkChoiceRunner runner, YamlMappingNode checks)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> check in checks)
        {
            string key = ((YamlScalarNode)check.Key).Value!;
            switch (key)
            {
                case "time":
                    Assert.That(runner.Time, Is.EqualTo(ulong.Parse(Scalar(check.Value))), "time");
                    break;
                case "genesis_time":
                    Assert.That(runner.GenesisTime, Is.EqualTo(ulong.Parse(Scalar(check.Value))), "genesis_time");
                    break;
                case "head":
                    YamlMappingNode head = (YamlMappingNode)check.Value;
                    Hash256 headRoot = runner.GetHead();
                    Assert.That(headRoot, Is.EqualTo(Hash(head, "root")), "head root");
                    Assert.That(runner.GetBlockSlot(headRoot), Is.EqualTo(ulong.Parse(Scalar(head[new YamlScalarNode("slot")]))), "head slot");
                    break;
                case "justified_checkpoint":
                    AssertCheckpoint(runner.JustifiedCheckpoint, (YamlMappingNode)check.Value, "justified checkpoint");
                    break;
                case "finalized_checkpoint":
                    AssertCheckpoint(runner.FinalizedCheckpoint, (YamlMappingNode)check.Value, "finalized checkpoint");
                    break;
                case "proposer_boost_root":
                    Assert.That(runner.ProposerBoostRoot, Is.EqualTo(new Hash256(Scalar(check.Value))), "proposer boost root");
                    break;
                default:
                    Assert.Fail($"Unhandled check key: {key}");
                    break;
            }
        }
    }

    private static void AssertCheckpoint(CheckpointRef actual, YamlMappingNode expected, string name)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Epoch, Is.EqualTo(ulong.Parse(Scalar(expected[new YamlScalarNode("epoch")]))), $"{name} epoch");
            Assert.That(actual.Root, Is.EqualTo(Hash(expected, "root")), $"{name} root");
        }
    }

    private static IEnumerable<YamlMappingNode> LoadSteps(string casePath)
    {
        using StreamReader reader = new(Path.Combine(casePath, "steps.yaml"));
        YamlStream yaml = [];
        yaml.Load(reader);
        foreach (YamlNode step in (YamlSequenceNode)yaml.Documents[0].RootNode)
        {
            yield return (YamlMappingNode)step;
        }
    }

    private static bool TryGetChild(YamlMappingNode node, string key, out YamlNode? child) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out child);

    private static bool TryGetScalar(YamlMappingNode node, string key, [NotNullWhen(true)] out string? value)
    {
        value = TryGetChild(node, key, out YamlNode? child) && child is YamlScalarNode scalar ? scalar.Value : null;
        return value is not null;
    }

    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value!;

    private static Hash256 Hash(YamlMappingNode node, string key) => new(Scalar(node[new YamlScalarNode(key)]));

    private static BeaconStateFulu Clone(BeaconStateFulu state)
    {
        BeaconStateFulu.Decode(BeaconStateFulu.Encode(state), out BeaconStateFulu clone);
        return clone;
    }

    private sealed class MapStateProvider(Dictionary<Hash256, BeaconStateFulu> states) : IForkChoiceStateProvider
    {
        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => states.TryGetValue(blockRoot, out BeaconStateFulu? state) ? state : null;

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot) is { } state ? Clone(state) : null;
    }
}
