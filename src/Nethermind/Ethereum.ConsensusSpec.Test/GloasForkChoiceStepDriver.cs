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
using Nethermind.Core.Crypto;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;
using static Ethereum.ConsensusSpec.Test.ForkChoiceStepDriver;

namespace Ethereum.ConsensusSpec.Test;

/// <remarks>Gloas on_block already applies body payload votes (specs/gloas/fork-choice.md notify_ptc_messages); do not replay them twice.</remarks>
internal static class GloasForkChoiceStepDriver
{
    private sealed class InMemoryStateProvider : IForkChoiceStateProvider, IGloasBlockStateProvider
    {
        public readonly Dictionary<Hash256, BeaconStateGloas> States = [];

        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => null;

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => null;

        public BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot) => States.GetValueOrDefault(blockRoot);
    }

    internal sealed class ValidPayloadNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;

        public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests) =>
            ExecutionStatus.Valid;
    }

    private sealed class Context(string casePath, BeaconChainSpec spec, ForkChoiceRunner runner, InMemoryStateProvider states, BeaconStateGloas anchorState, PubkeyCache pubkeys, bool verifySignatures)
    {
        public BeaconChainSpec Spec => spec;
        public ForkChoiceRunner Runner => runner;
        public InMemoryStateProvider States => states;
        public BeaconStateGloas AnchorState => anchorState;
        public PubkeyCache Pubkeys => pubkeys;
        public bool VerifySignatures => verifySignatures;

        public byte[] Read(string key) => SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, key + ".ssz_snappy"));
    }

    private static readonly ForkDriver<BeaconStateGloas> Transition = (ForkDriver<BeaconStateGloas>)ForkDriver.ByName["gloas"];

    public static ForkChoiceRunner Run(string casePath)
    {
        BeaconStateGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "anchor_state.ssz_snappy")), out BeaconStateGloas anchorState);
        BeaconBlockGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "anchor_block.ssz_snappy")), out BeaconBlockGloas anchorBlock);

        BeaconChainSpec spec = CaseSpec(casePath);
        PubkeyCache pubkeys = FuluDriverSupport.BuildPubkeyCache(anchorState.Validators!);
        InMemoryStateProvider states = new();
        states.States[SszRoots.HashTreeRoot(anchorBlock)] = anchorState;
        ForkChoiceRunner runner = new(spec, anchorState, anchorBlock, states, pubkeys, states);
        Context context = new(casePath, spec, runner, states, anchorState, pubkeys, FuluDriverSupport.ShouldVerifySignatures(casePath));

        YamlSequenceNode steps = LoadSteps(Path.Combine(casePath, "steps.yaml"));
        for (int i = 0; i < steps.Children.Count; i++)
            RunStep(context, (YamlMappingNode)steps.Children[i], i);

        return runner;
    }

    private static BeaconChainSpec CaseSpec(string casePath) =>
        File.Exists(Path.Combine(casePath, "config.yaml")) ? FuluDriverSupport.CaseSpec(casePath) : FuluDriverSupport.TransitionSpec(0);

    private static void RunStep(Context context, YamlMappingNode step, int stepIndex)
    {
        bool valid = GetBool(step, "valid", defaultValue: true);
        if (TryGetScalar(step, "tick", out string? tick))
            context.Runner.OnTick(context.Runner.GenesisTime + ulong.Parse(tick!));
        else if (TryGetScalar(step, "block", out string? blockKey))
            RunBlockStep(context, blockKey!, valid, stepIndex);
        else if (TryGetScalar(step, "execution_payload", out string? envelopeKey))
            RunEnvelopeStep(context, envelopeKey!, valid, stepIndex);
        else if (TryGetScalar(step, "attestation", out string? attestationKey))
            RunAttestationStep(context, attestationKey!, valid, stepIndex);
        else if (TryGetScalar(step, "attester_slashing", out string? slashingKey))
            RunAttesterSlashingStep(context, slashingKey!, valid, stepIndex);
        else if (TryGetScalar(step, "payload_attestation_message", out string? messageKey))
            RunPayloadAttestationStep(context, messageKey!, valid, stepIndex);
        else if (TryGetChild(step, "checks", out YamlNode? checks))
            RunChecksStep(context, (YamlMappingNode)checks!, stepIndex);
        else
            throw new NotImplementedInDriverException($"step {stepIndex}: unrecognized step shape '{string.Join(",", Keys(step))}' has no entry point in this driver.");
    }

    /// <summary>Transitions a parent-state copy and replays the pyspec body; unknown parents go directly to on_block for rejection.</summary>
    private static void RunBlockStep(Context context, string key, bool expectedValid, int stepIndex)
    {
        byte[] ssz = context.Read(key);
        SignedBeaconBlockGloas.Decode(ssz, out SignedBeaconBlockGloas signedBlock);
        BeaconBlockGloas block = signedBlock.Message!;
        Hash256 blockRoot = SszRoots.HashTreeRoot(block);

        BeaconStateGloas? postState = null;
        Exception? rejection = Attempt(() =>
        {
            if (context.States.States.TryGetValue(block.ParentRoot!, out BeaconStateGloas? parentState))
            {
                postState = parentState.Clone();
                Transition.ApplyBlock(postState, ssz, context.Spec, Transition.NewCache(), context.Pubkeys, new ValidPayloadNotifier(), context.VerifySignatures);
            }

            context.Runner.OnBlock(signedBlock, postState ?? context.AnchorState);
        });

        string subject = $"step {stepIndex}: block {key} (slot {block.Slot})";
        AssertVerdict(subject, expectedValid, rejection);
        if (rejection is not null)
            return;

        context.States.States[blockRoot] = postState ?? throw new InvalidOperationException($"{subject} was accepted without a parent state");

        AttestationGloas[] attestations = block.Body!.Attestations!;
        for (int i = 0; i < attestations.Length; i++)
        {
            AttestationGloas attestation = attestations[i];
            if (Attempt(() => context.Runner.OnBodyAttestation(attestation, blockRoot)) is { } ex)
                Assert.Fail($"{subject}: body attestation {i} rejected: {ex.Message}");
        }

        // A vote waiting for a build would reach the store only on a later tick, after the vector's checks of this step.
        Assert.That(context.Runner.DeferredBodyVoteCount, Is.Zero, $"{subject} left body attestations waiting for a target state build");

        AttesterSlashingGloas[] slashings = block.Body!.AttesterSlashings!;
        for (int i = 0; i < slashings.Length; i++)
        {
            AttesterSlashingGloas slashing = slashings[i];
            if (Attempt(() => context.Runner.OnAttesterSlashing(slashing, verifySignatures: false)) is { } ex)
                Assert.Fail($"{subject}: body attester slashing {i} rejected: {ex.Message}");
        }
    }

    /// <summary>Verifies an envelope against its block's post-state and records its payload; without sidecars only a no-blob bid is available.</summary>
    private static void RunEnvelopeStep(Context context, string key, bool expectedValid, int stepIndex)
    {
        SignedExecutionPayloadEnvelope.Decode(context.Read(key), out SignedExecutionPayloadEnvelope signedEnvelope);
        Hash256? blockRoot = signedEnvelope.Message!.BeaconBlockRoot;
        if (blockRoot is not null && context.States.GetGloasBlockState(blockRoot) is { } state && state.LatestExecutionPayloadBid!.BlobKzgCommitments!.Length > 0)
            throw new NotImplementedInDriverException($"step {stepIndex}: envelope {key} commits to blobs, and its steps carry no column sidecars to make them available");

        Exception? rejection = Attempt(() =>
        {
            GloasBlockProcessing.VerifyExecutionPayloadEnvelope(context.States, signedEnvelope, new ValidPayloadNotifier(), context.Pubkeys);
            context.Runner.OnExecutionPayloadVerified(blockRoot!);
        });

        AssertVerdict($"step {stepIndex}: execution_payload {key}", expectedValid, rejection);
    }

    private static void RunAttestationStep(Context context, string key, bool expectedValid, int stepIndex)
    {
        AttestationGloas.Decode(context.Read(key), out AttestationGloas attestation);
        AssertVerdict($"step {stepIndex}: attestation {key}", expectedValid,
            Attempt(() => context.Runner.OnAttestation(attestation, isFromBlock: false, verifySignature: true)));
    }

    private static void RunPayloadAttestationStep(Context context, string key, bool expectedValid, int stepIndex)
    {
        PayloadAttestationMessage.Decode(context.Read(key), out PayloadAttestationMessage message);
        AssertVerdict($"step {stepIndex}: payload_attestation_message {key}", expectedValid,
            Attempt(() => context.Runner.OnPayloadAttestationMessage(message, isFromBlock: false, verifySignature: context.VerifySignatures)));
    }

    private static void RunAttesterSlashingStep(Context context, string key, bool expectedValid, int stepIndex)
    {
        AttesterSlashingGloas.Decode(context.Read(key), out AttesterSlashingGloas slashing);
        AssertVerdict($"step {stepIndex}: attester_slashing {key}", expectedValid,
            Attempt(() => context.Runner.OnAttesterSlashing(slashing, verifySignatures: true)));
    }

    private static void RunChecksStep(Context context, YamlMappingNode checks, int stepIndex)
    {
        ForkChoiceRunner runner = context.Runner;
        foreach (string key in Keys(checks))
        {
            switch (key)
            {
                case "time":
                    AssertEqual(stepIndex, key, ulong.Parse(GetScalar(checks, key)), runner.Time - runner.GenesisTime);
                    break;
                case "genesis_time":
                    AssertEqual(stepIndex, key, ulong.Parse(GetScalar(checks, key)), runner.GenesisTime);
                    break;
                case "head":
                    TryGetChild(checks, key, out YamlNode? headNode);
                    YamlMappingNode head = (YamlMappingNode)headNode!;
                    ForkChoiceNode actualHead = runner.GetHeadNode();
                    AssertEqual(stepIndex, "head.root", new Hash256(GetScalar(head, "root")), actualHead.Root);
                    AssertEqual(stepIndex, "head.slot", (ulong?)ulong.Parse(GetScalar(head, "slot")), runner.GetBlockSlot(actualHead.Root));
                    AssertEqual(stepIndex, "head.payload_status", (ForkChoicePayloadStatus)byte.Parse(GetScalar(head, "payload_status")), actualHead.PayloadStatus);
                    break;
                case "justified_checkpoint":
                case "finalized_checkpoint":
                    TryGetChild(checks, key, out YamlNode? checkpoint);
                    AssertCheckpoint(key, (YamlMappingNode)checkpoint!, key == "justified_checkpoint" ? runner.JustifiedCheckpoint : runner.FinalizedCheckpoint, stepIndex);
                    break;
                case "proposer_boost_root":
                    AssertEqual(stepIndex, key, new Hash256(GetScalar(checks, key)), runner.ProposerBoostRoot);
                    break;
                case "payload_timeliness_vote":
                case "payload_data_availability_vote":
                    TryGetChild(checks, key, out YamlNode? voteNode);
                    AssertPtcVotes(runner, stepIndex, key, (YamlMappingNode)voteNode!);
                    break;
                default:
                    throw new NotImplementedInDriverException($"step {stepIndex}: check '{key}' has no entry point in this driver.");
            }
        }
    }

    /// <summary>A <c>store.payload_timeliness_vote</c> or <c>store.payload_data_availability_vote</c> check: every seat's vote, <c>null</c> for no vote.</summary>
    private static void AssertPtcVotes(ForkChoiceRunner runner, int stepIndex, string key, YamlMappingNode check)
    {
        Hash256 root = new(GetScalar(check, "block_root"));
        if (runner.GetPtcVotes(root) is not { } votes)
        {
            Assert.Fail($"step {stepIndex}: checks.{key} names {root}, for which fork choice keeps no PTC votes");
            return;
        }

        TryGetChild(check, "votes", out YamlNode? expectedNode);
        string[] expected = [.. ((YamlSequenceNode)expectedNode!).Children.Select(static v => ((YamlScalarNode)v).Value!)];
        IReadOnlyList<bool?> actual = key == "payload_timeliness_vote" ? votes.Timeliness : votes.DataAvailability;
        string[] actualText = [.. actual.Select(static v => v is bool b ? (b ? "true" : "false") : "null")];
        Assert.That(actualText, Is.EqualTo(expected), $"step {stepIndex}: checks.{key} for {root}");
    }
}
