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

/// <summary>
/// Replays one Gloas <c>fork_choice</c> vector's <c>steps.yaml</c> against a <see cref="ForkChoiceRunner"/> rooted at the
/// vector's Gloas anchor: on_tick, on_block through the Gloas state transition, on_execution_payload_envelope, on_attestation,
/// on_attester_slashing and the time, head root and slot, checkpoint and proposer boost checks, honoring each step's
/// <c>valid</c> flag.
/// </summary>
/// <remarks>
/// The runner's head is a block root, whereas the Gloas <c>get_head</c> returns a <c>ForkChoiceNode</c> with a payload status,
/// and it has no <c>on_payload_attestation_message</c> or PTC vote store (specs/gloas/fork-choice.md). Steps and checks that
/// need these are recorded in <see cref="GloasForkChoiceRun.Unsupported"/> and every other step still runs, so a caller can
/// report the vector not implemented only after the rest of it has been checked.
/// </remarks>
internal static class GloasForkChoiceStepDriver
{
    private sealed class InMemoryStateProvider : IForkChoiceStateProvider, IGloasBlockStateProvider
    {
        public readonly Dictionary<Hash256, BeaconStateGloas> States = [];

        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => null;

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => null;

        public BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot) => States.GetValueOrDefault(blockRoot);
    }

    /// <summary>The execution layer of the pyspec harness, which accepts every payload.</summary>
    internal sealed class ValidPayloadNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;

        public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests) =>
            ExecutionStatus.Valid;
    }

    private sealed class Context(string casePath, BeaconChainSpec spec, ForkChoiceRunner runner, InMemoryStateProvider states, BeaconStateGloas anchorState, PubkeyCache pubkeys, bool verifySignatures, bool headNeedsPayloadStatus)
    {
        public BeaconChainSpec Spec => spec;
        public ForkChoiceRunner Runner => runner;
        public InMemoryStateProvider States => states;
        public BeaconStateGloas AnchorState => anchorState;
        public PubkeyCache Pubkeys => pubkeys;
        public bool VerifySignatures => verifySignatures;
        public bool HeadNeedsPayloadStatus => headNeedsPayloadStatus;
        public readonly SortedSet<string> Unsupported = new(StringComparer.Ordinal);
        public readonly List<int> HeadDivergences = [];

        public byte[] Read(string key) => SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, key + ".ssz_snappy"));
    }

    private static readonly ForkDriver<BeaconStateGloas> Transition = (ForkDriver<BeaconStateGloas>)ForkDriver.ByName["gloas"];

    /// <param name="headNeedsPayloadStatus">
    /// The vector's head checks name a block the Gloas <c>get_head</c> reaches only by weighing EMPTY against FULL nodes, so a
    /// head root or slot that differs is recorded in <see cref="GloasForkChoiceRun.HeadDivergences"/> instead of failing.
    /// </param>
    public static GloasForkChoiceRun Run(string casePath, bool headNeedsPayloadStatus = false)
    {
        BeaconStateGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "anchor_state.ssz_snappy")), out BeaconStateGloas anchorState);
        BeaconBlockGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "anchor_block.ssz_snappy")), out BeaconBlockGloas anchorBlock);

        BeaconChainSpec spec = CaseSpec(casePath);
        PubkeyCache pubkeys = FuluDriverSupport.BuildPubkeyCache(anchorState.Validators!);
        InMemoryStateProvider states = new();
        states.States[SszRoots.HashTreeRoot(anchorBlock)] = anchorState;
        ForkChoiceRunner runner = new(spec, anchorState, anchorBlock, states, pubkeys, states);
        Context context = new(casePath, spec, runner, states, anchorState, pubkeys, FuluDriverSupport.ShouldVerifySignatures(casePath), headNeedsPayloadStatus);

        YamlSequenceNode steps = LoadSteps(Path.Combine(casePath, "steps.yaml"));
        for (int i = 0; i < steps.Children.Count; i++)
            RunStep(context, (YamlMappingNode)steps.Children[i], i);

        return new GloasForkChoiceRun(runner, context.Unsupported, context.HeadDivergences);
    }

    /// <summary>The vector's own <c>config.yaml</c> when present, otherwise the mainnet config with Gloas live from genesis, the fork the vector's spec module is.</summary>
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
        else if (TryGetScalar(step, "payload_attestation_message", out _))
            context.Unsupported.Add("on_payload_attestation_message");
        else if (TryGetChild(step, "checks", out YamlNode? checks))
            RunChecksStep(context, (YamlMappingNode)checks!, stepIndex);
        else
            throw new NotImplementedInDriverException($"step {stepIndex}: unrecognized step shape '{string.Join(",", Keys(step))}' has no entry point in this driver.");
    }

    /// <summary>
    /// The Gloas state transition on a copy of the parent's post-state, then <c>on_block</c> and the body replay of the
    /// pyspec harness's <c>add_block</c>; a block whose parent state is unknown goes to fork choice with the anchor state,
    /// as <c>on_block</c> refuses an unknown parent before any state transition.
    /// </summary>
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
            if (Attempt(() => context.Runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false)) is { } ex)
                Assert.Fail($"{subject}: body attestation {i} rejected: {ex.Message}");
        }

        AttesterSlashingGloas[] slashings = block.Body!.AttesterSlashings!;
        for (int i = 0; i < slashings.Length; i++)
        {
            AttesterSlashingGloas slashing = slashings[i];
            if (Attempt(() => context.Runner.OnAttesterSlashing(slashing, verifySignatures: false)) is { } ex)
                Assert.Fail($"{subject}: body attester slashing {i} rejected: {ex.Message}");
        }

        if (block.Body!.PayloadAttestations!.Length > 0)
            context.Unsupported.Add("on_payload_attestation_message (body payload attestations)");
    }

    /// <summary>
    /// The spec's <c>on_execution_payload_envelope</c>: verification against the post-state of the block the envelope names,
    /// then the <c>store.payloads</c> write. Only an envelope whose bid commits to no blobs is available without sidecars.
    /// </summary>
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

    private static void RunAttesterSlashingStep(Context context, string key, bool expectedValid, int stepIndex)
    {
        AttesterSlashingGloas.Decode(context.Read(key), out AttesterSlashingGloas slashing);
        AssertVerdict($"step {stepIndex}: attester_slashing {key}", expectedValid,
            Attempt(() => context.Runner.OnAttesterSlashing(slashing, verifySignatures: true)));
    }

    private static Exception? Attempt(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>An expected rejection that came from anything but a spec assertion is a crash the vector happened to want, not a pass.</summary>
    private static void AssertVerdict(string subject, bool expectedValid, Exception? rejection)
    {
        if (rejection is null && !expectedValid)
            Assert.Fail($"{subject} was expected to be REJECTED but the driver accepted it");
        if (rejection is not null && expectedValid)
            Assert.Fail($"{subject} was expected to be accepted but the driver rejected it: {rejection.Message}");
        if (rejection is not null && !FuluDriverSupport.IsSpecRejection(rejection))
            Assert.Fail($"{subject} was rejected by {rejection.GetType().Name} rather than a spec assertion: {rejection}");
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
                    Hash256 actualHead = runner.GetHead();
                    Hash256 expectedHead = new(GetScalar(head, "root"));
                    if (context.HeadNeedsPayloadStatus && actualHead != expectedHead)
                    {
                        AssertFullAncestorHead(context, stepIndex, head, expectedHead, actualHead);
                        context.HeadDivergences.Add(stepIndex);
                        context.Unsupported.Add("get_head over EMPTY and FULL nodes");
                        break;
                    }

                    AssertEqual(stepIndex, "head.root", expectedHead, actualHead);
                    AssertEqual(stepIndex, "head.slot", (ulong?)ulong.Parse(GetScalar(head, "slot")), runner.GetBlockSlot(actualHead));
                    if (TryGetScalar(head, "payload_status", out string? payloadStatus))
                        CheckHeadPayloadStatus(context, stepIndex, actualHead, byte.Parse(payloadStatus!));
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
                    context.Unsupported.Add($"store.{key}");
                    break;
                default:
                    throw new NotImplementedInDriverException($"step {stepIndex}: check '{key}' has no entry point in this driver.");
            }
        }
    }

    private static void AssertFullAncestorHead(Context context, int stepIndex, YamlMappingNode head, Hash256 expectedHead, Hash256 actualHead)
    {
        bool full = TryGetScalar(head, "payload_status", out string? status) && byte.Parse(status!) == PayloadStatusFull;
        if (!IsWaivedHeadDivergence(context.Runner, full, expectedHead, actualHead))
        {
            Assert.Fail($"step {stepIndex}: checks.head expected {expectedHead}, actual {actualHead}; the waiver covers only the FULL node of a verified ancestor " +
                $"(payload_status FULL {full}, payload verified {context.Runner.IsPayloadVerified(expectedHead)})");
        }
    }

    /// <summary>
    /// Whether a waived head check may name <paramref name="expectedHead"/> instead of the runner's <paramref name="actualHead"/>:
    /// only the FULL node of a verified strict ancestor. Any other divergence is a head the runner got wrong for a reason the
    /// waiver does not cover.
    /// </summary>
    internal static bool IsWaivedHeadDivergence(ForkChoiceRunner runner, bool expectsFull, Hash256 expectedHead, Hash256 actualHead)
    {
        bool ancestor = runner.EnumerateAncestors(actualHead).Skip(1).Any(node => node.Root == expectedHead);
        return expectsFull && runner.IsPayloadVerified(expectedHead) && ancestor;
    }

    private static void CheckHeadPayloadStatus(Context context, int stepIndex, Hash256 head, byte expected)
    {
        switch (DecideHeadPayloadStatus(expected, context.Runner.IsPayloadVerified(head)))
        {
            case PayloadStatusVerdict.Undecided:
                context.Unsupported.Add(expected == PayloadStatusFull ? "get_head FULL over EMPTY" : "get_head EMPTY over FULL");
                break;
            case PayloadStatusVerdict.Contradicted:
                Assert.Fail($"step {stepIndex}: checks.head.payload_status {expected} cannot be the status of head {head}, whose payload verified is {context.Runner.IsPayloadVerified(head)}");
                break;
        }
    }

    internal const byte PayloadStatusEmpty = 0;
    internal const byte PayloadStatusFull = 1;
    internal const byte PayloadStatusPending = 2;

    internal enum PayloadStatusVerdict { Holds, Undecided, Contradicted }

    /// <summary>Whether the runner's store decides a head's expected payload status, given whether the head's payload is verified.</summary>
    /// <remarks>
    /// A root absent from <c>store.payloads</c> has no FULL node, so its head node is EMPTY; with both nodes present the
    /// choice weighs PTC votes the runner does not keep. <c>get_head</c> never returns a PENDING node (specs/gloas/fork-choice.md
    /// get_node_children, get_head).
    /// </remarks>
    internal static PayloadStatusVerdict DecideHeadPayloadStatus(byte expected, bool payloadVerified) => expected switch
    {
        PayloadStatusEmpty => payloadVerified ? PayloadStatusVerdict.Undecided : PayloadStatusVerdict.Holds,
        PayloadStatusFull => payloadVerified ? PayloadStatusVerdict.Undecided : PayloadStatusVerdict.Contradicted,
        _ => PayloadStatusVerdict.Contradicted,
    };

    private static void AssertEqual<T>(int stepIndex, string check, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            Assert.Fail($"step {stepIndex}: checks.{check} expected {expected}, actual {actual}");
    }
}

/// <summary>The outcome of a Gloas fork_choice vector whose every step ran without a failed check.</summary>
/// <param name="Unsupported">The spec functions and store fields the vector needed that the runner has no entry point for.</param>
/// <param name="HeadDivergences">The steps whose head check named another block than the runner's head.</param>
internal sealed record GloasForkChoiceRun(ForkChoiceRunner Runner, IReadOnlyCollection<string> Unsupported, IReadOnlyList<int> HeadDivergences);
