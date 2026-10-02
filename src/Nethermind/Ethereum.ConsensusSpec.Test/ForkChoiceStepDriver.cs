// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Replays one <c>fork_choice</c> or <c>sync</c> vector's <c>steps.yaml</c> script against
/// <see cref="ForkChoiceRunner"/>: on_tick, on_block (via the real <see cref="StateTransition"/>
/// pipeline), on_attestation, on_attester_slashing, on_payload_info and checks (head, justified/finalized
/// checkpoints, proposer boost root, get_proposer_head) and PeerDAS column-sidecar availability.
/// Honors each step's <c>valid</c> flag - an invalid step must be rejected, and acceptance is
/// reported as a failure, never silently treated as a pass. Step shapes with no entry point in
/// this driver throw <see cref="NotImplementedInDriverException"/>, named, rather than being
/// skipped.
/// </summary>
/// <remarks>
/// A block the execution layer declared INVALID through on_payload_info is refused, and its ancestors back to
/// <c>latest_valid_hash</c> are invalidated. Its body is still replayed: the pyspec harness's <c>add_block</c> with
/// <c>is_optimistic</c> stores it and replays its attestations and attester slashings before marking the step
/// invalid, whereas a refused block in the fork_choice format replays nothing.
/// </remarks>
internal static class ForkChoiceStepDriver
{
    private sealed class InMemoryStateProvider(BeaconStateFulu anchor) : IForkChoiceStateProvider
    {
        public readonly Dictionary<Hash256, BeaconStateFulu> States = [];

        public BeaconStateFulu Anchor => anchor;

        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) =>
            States.TryGetValue(blockRoot, out BeaconStateFulu? state) ? state : null;

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();
    }

    private sealed class FixedNewPayloadNotifier(ExecutionStatus status) : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => status;
    }

    /// <summary>An on_payload_info step: the execution layer's answer for one payload, by <c>payloadStatus</c> field.</summary>
    internal sealed record PayloadInfo(string Status, Hash256? LatestValidHash)
    {
        public bool IsInvalid => Status is "INVALID" or "INVALID_BLOCK_HASH";

        public ExecutionStatus ExecutionStatus => Status switch
        {
            "VALID" => ExecutionStatus.Valid,
            "SYNCING" or "ACCEPTED" => ExecutionStatus.Optimistic,
            _ when IsInvalid => ExecutionStatus.Invalid,
            _ => throw new InvalidDataException($"unknown payload status '{Status}'"),
        };
    }

    /// <returns>The fork choice store after the last step, for callers that inspect more than the vector's own checks.</returns>
    public static ForkChoiceRunner Run(string casePath) => Run(casePath, out _);

    /// <inheritdoc cref="Run(string)"/>
    /// <param name="blockRejections">Why each block step, in order, was refused; <c>null</c> for an accepted block.</param>
    internal static ForkChoiceRunner Run(string casePath, out List<Exception?> blockRejections)
    {
        BeaconStateFulu anchorState = FuluDriverSupport.DecodeState(Path.Combine(casePath, "anchor_state.ssz_snappy"));
        byte[] anchorBlockSsz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "anchor_block.ssz_snappy"));
        BeaconBlock.Decode(anchorBlockSsz, out BeaconBlock anchorBlock);

        PubkeyCache pubkeys = FuluDriverSupport.BuildPubkeyCache(anchorState.Validators!);
        InMemoryStateProvider stateProvider = new(anchorState);
        Hash256 anchorRoot = SszRoots.HashTreeRoot(anchorBlock);
        stateProvider.States[anchorRoot] = anchorState;

        ForkChoiceRunner runner = new(BeaconChainSpec.Mainnet, anchorState, anchorBlock, stateProvider, pubkeys);
        bool executionValid = FuluDriverSupport.ReadExecutionValid(casePath);
        Dictionary<Hash256, PayloadInfo> payloadInfos = [];
        blockRejections = [];

        YamlSequenceNode steps = LoadSteps(Path.Combine(casePath, "steps.yaml"));
        int stepIndex = 0;
        foreach (YamlNode stepNode in steps.Children)
        {
            RunStep(casePath, (YamlMappingNode)stepNode, runner, stateProvider, pubkeys, stepIndex, executionValid, payloadInfos, blockRejections);
            stepIndex++;
        }

        return runner;
    }

    internal static YamlSequenceNode LoadSteps(string path)
    {
        using StreamReader reader = new(path);
        YamlStream yaml = [];
        yaml.Load(reader);
        return (YamlSequenceNode)yaml.Documents[0].RootNode;
    }

    private static void RunStep(string casePath, YamlMappingNode step, ForkChoiceRunner runner, InMemoryStateProvider stateProvider, PubkeyCache pubkeys, int stepIndex, bool executionValid, Dictionary<Hash256, PayloadInfo> payloadInfos, List<Exception?> blockRejections)
    {
        if (TryGetScalar(step, "tick", out string? tickValue))
        {
            runner.OnTick(runner.GenesisTime + ulong.Parse(tickValue!));
            return;
        }

        if (TryGetScalar(step, "block_hash", out string? blockHash) && TryGetChild(step, "payload_status", out YamlNode? statusNode))
        {
            YamlMappingNode status = (YamlMappingNode)statusNode!;
            TryGetScalar(status, "latest_valid_hash", out string? latestValidHash);
            payloadInfos[new Hash256(blockHash!)] = new PayloadInfo(GetScalar(status, "status"), string.IsNullOrEmpty(latestValidHash) || latestValidHash == "null" ? null : new Hash256(latestValidHash));
            return;
        }

        if (TryGetScalar(step, "block", out string? blockKey))
        {
            DataColumnSidecar[]? dataColumns = TryGetChild(step, "columns", out YamlNode? columnsNode)
                ? LoadDataColumnSidecars(casePath, (YamlSequenceNode)columnsNode!)
                : null;

            blockRejections.Add(RunBlockStep(casePath, blockKey!, GetBool(step, "valid", defaultValue: true), runner, stateProvider, pubkeys, stepIndex, dataColumns, executionValid, payloadInfos));
            return;
        }

        if (TryGetScalar(step, "attestation", out string? attestationKey))
        {
            RunAttestationStep(casePath, attestationKey!, GetBool(step, "valid", defaultValue: true), runner, stepIndex);
            return;
        }

        if (TryGetScalar(step, "attester_slashing", out string? slashingKey))
        {
            RunAttesterSlashingStep(casePath, slashingKey!, GetBool(step, "valid", defaultValue: true), runner, stepIndex);
            return;
        }

        if (TryGetChild(step, "checks", out YamlNode? checksNode))
        {
            RunChecksStep((YamlMappingNode)checksNode!, runner, stepIndex);
            return;
        }

        throw new NotImplementedInDriverException($"step {stepIndex}: unrecognized step shape '{string.Join(",", Keys(step))}' has no entry point in this driver.");
    }

    /// <summary>
    /// Applies the block's state transition on a clone of its parent's post-state (never the
    /// parent itself - forks must not share mutable state) with a fresh <see cref="EpochCache"/>
    /// per call, since its balance memo is documented as not fork-aware (see BlockImporter's own
    /// "fork branch: stateless hasher, fresh balance memo" comment for the same rule in production
    /// code) and this driver deliberately explores conflicting branches within one vector.
    /// </summary>
    /// <returns>Why the block was refused, or <c>null</c>.</returns>
    private static Exception? RunBlockStep(string casePath, string blockKey, bool expectedValid, ForkChoiceRunner runner, InMemoryStateProvider stateProvider, PubkeyCache pubkeys, int stepIndex, DataColumnSidecar[]? dataColumns, bool executionValid, Dictionary<Hash256, PayloadInfo> payloadInfos)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, blockKey + ".ssz_snappy"));
        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock signedBlock);
        BeaconBlock block = signedBlock.Message!;
        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        PayloadInfo? payloadInfo = payloadInfos.GetValueOrDefault(block.Body!.ExecutionPayload!.BlockHash!);
        ExecutionStatus executionStatus = payloadInfo?.ExecutionStatus ?? (executionValid ? ExecutionStatus.Valid : ExecutionStatus.Invalid);

        bool accepted;
        string? rejectionReason = null;
        Exception? rejection = null;
        BeaconStateFulu? postState = null;
        try
        {
            // on_block asserts the parent is known before any state transition, so a block with no parent
            // state goes to fork choice with the anchor state standing in for the post-state it cannot have.
            if (stateProvider.States.TryGetValue(block.ParentRoot!, out BeaconStateFulu? parentState))
            {
                postState = parentState.Clone();
                StateTransition.Apply(postState, signedBlock, new EpochCache(), pubkeys, new FixedNewPayloadNotifier(executionStatus), BeaconChainSpec.Mainnet, validateResult: true, verifySignatures: true);
            }

            runner.OnBlock(signedBlock, postState ?? stateProvider.Anchor, payloadInfo is null ? ExecutionStatus.Valid : executionStatus, dataColumns);
            accepted = true;
        }
        catch (Exception ex)
        {
            accepted = false;
            rejection = ex;
            rejectionReason = ex.Message;
        }

        if (accepted && !expectedValid)
            Assert.Fail($"step {stepIndex}: block {blockKey} (slot {block.Slot}) was expected to be REJECTED but the driver accepted it");
        if (!accepted && expectedValid)
            Assert.Fail($"step {stepIndex}: block {blockKey} (slot {block.Slot}) was expected to be accepted but the driver rejected it: {rejectionReason}");
        AssertRejectedForASpecReason(rejection, $"step {stepIndex}: block {blockKey} (slot {block.Slot})");

        if (payloadInfo is { IsInvalid: true })
            InvalidateBackToLatestValidHash(runner, block.ParentRoot!, payloadInfo.LatestValidHash);
        else if (!accepted)
            return rejection;
        else
            stateProvider.States[blockRoot] = postState ?? throw new InvalidOperationException($"step {stepIndex}: block {blockKey} was accepted without a parent state");

        // The fork_choice and sync test formats treat an on_block step as implying on_attestation(is_from_block)
        // for every body attestation and on_attester_slashing for every body slashing.
        Attestation[] bodyAttestations = block.Body!.Attestations!;
        for (int i = 0; i < bodyAttestations.Length; i++)
        {
            try { runner.OnBodyAttestation(bodyAttestations[i], blockRoot); }
            catch (Exception ex) { Assert.Fail($"step {stepIndex}: body attestation {i} of block {blockKey} rejected: {ex.Message}"); }
        }

        // A vote waiting for a build would reach the store only on a later tick, after the vector's checks of this step.
        Assert.That(runner.DeferredBodyVoteCount, Is.Zero, $"step {stepIndex}: block {blockKey} left body attestations waiting for a target state build");

        AttesterSlashing[] bodySlashings = block.Body!.AttesterSlashings!;
        for (int i = 0; i < bodySlashings.Length; i++)
        {
            try { runner.OnAttesterSlashing(bodySlashings[i], verifySignatures: false); }
            catch (Exception ex) { Assert.Fail($"step {stepIndex}: body attester slashing {i} of block {blockKey} rejected: {ex.Message}"); }
        }

        return rejection;
    }

    /// <summary>
    /// Marks the ancestors of an INVALID payload invalid up to the one whose payload hash is <paramref name="latestValidHash"/>, as
    /// the pyspec harness's <c>add_optimistic_block</c> does; a parent that is itself the latest valid payload keeps its status.
    /// </summary>
    private static void InvalidateBackToLatestValidHash(ForkChoiceRunner runner, Hash256 parentRoot, Hash256? latestValidHash)
    {
        if (latestValidHash is not null && runner.ContainsBlock(parentRoot) && runner.GetExecutionBlockHash(parentRoot) != latestValidHash)
            runner.OnInvalidExecutionPayload(parentRoot, latestValidHash);
    }

    /// <summary>Decodes the PeerDAS 'columns' sequence of a block step into the sidecars <see cref="ForkChoiceRunner.OnBlock"/> checks the block's data availability against.</summary>
    private static DataColumnSidecar[] LoadDataColumnSidecars(string casePath, YamlSequenceNode columns)
    {
        DataColumnSidecar[] sidecars = new DataColumnSidecar[columns.Children.Count];
        for (int i = 0; i < sidecars.Length; i++)
        {
            string key = ((YamlScalarNode)columns.Children[i]).Value!;
            byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, key + ".ssz_snappy"));
            DataColumnSidecar.Decode(ssz, out DataColumnSidecar sidecar);
            sidecars[i] = sidecar;
        }

        return sidecars;
    }

    private static void RunAttestationStep(string casePath, string key, bool expectedValid, ForkChoiceRunner runner, int stepIndex)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, key + ".ssz_snappy"));
        Attestation.Decode(ssz, out Attestation attestation);

        bool accepted = true;
        Exception? rejection = null;
        try { runner.OnAttestation(attestation, isFromBlock: false, verifySignature: true); }
        catch (Exception ex) { accepted = false; rejection = ex; }

        if (accepted && !expectedValid)
            Assert.Fail($"step {stepIndex}: attestation {key} was expected to be REJECTED but the driver accepted it");
        if (!accepted && expectedValid)
            Assert.Fail($"step {stepIndex}: attestation {key} was expected to be accepted but the driver rejected it: {rejection!.Message}");
        AssertRejectedForASpecReason(rejection, $"step {stepIndex}: attestation {key}");
    }

    private static void RunAttesterSlashingStep(string casePath, string key, bool expectedValid, ForkChoiceRunner runner, int stepIndex)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, key + ".ssz_snappy"));
        AttesterSlashing.Decode(ssz, out AttesterSlashing slashing);

        bool accepted = true;
        Exception? rejection = null;
        try { runner.OnAttesterSlashing(slashing, verifySignatures: true); }
        catch (Exception ex) { accepted = false; rejection = ex; }

        if (accepted && !expectedValid)
            Assert.Fail($"step {stepIndex}: attester_slashing {key} was expected to be REJECTED but the driver accepted it");
        if (!accepted && expectedValid)
            Assert.Fail($"step {stepIndex}: attester_slashing {key} was expected to be accepted but the driver rejected it: {rejection!.Message}");
        AssertRejectedForASpecReason(rejection, $"step {stepIndex}: attester_slashing {key}");
    }

    /// <summary>An expected rejection that came from anything but a spec assertion is a crash the vector happened to want, not a pass.</summary>
    private static void AssertRejectedForASpecReason(Exception? rejection, string subject)
    {
        if (rejection is not null && !FuluDriverSupport.IsSpecRejection(rejection))
            Assert.Fail($"{subject} was rejected by {rejection.GetType().Name} rather than a spec assertion: {rejection}");
    }

    private static void RunChecksStep(YamlMappingNode checks, ForkChoiceRunner runner, int stepIndex)
    {
        if (TryGetScalar(checks, "get_proposer_head", out string? proposerHeadRoot))
        {
            Hash256 expected = new(proposerHeadRoot!);
            Hash256 actual = runner.GetProposerHead(runner.GetHead(), runner.CurrentSlot);
            if (actual != expected)
                Assert.Fail($"step {stepIndex}: checks.get_proposer_head expected {expected}, actual {actual}");
        }

        if (TryGetScalar(checks, "time", out string? time))
        {
            ulong expected = ulong.Parse(time!);
            ulong actual = runner.Time - runner.GenesisTime;
            if (actual != expected)
                Assert.Fail($"step {stepIndex}: checks.time expected {expected}, actual {actual}");
        }

        if (TryGetScalar(checks, "genesis_time", out string? genesisTime))
        {
            ulong expected = ulong.Parse(genesisTime!);
            if (runner.GenesisTime != expected)
                Assert.Fail($"step {stepIndex}: checks.genesis_time expected {expected}, actual {runner.GenesisTime}");
        }

        if (TryGetChild(checks, "head", out YamlNode? headNode))
        {
            YamlMappingNode head = (YamlMappingNode)headNode!;
            Hash256 expectedRoot = new(GetScalar(head, "root"));
            ulong expectedSlot = ulong.Parse(GetScalar(head, "slot"));
            Hash256 actualHead = runner.GetHead();
            ulong? actualSlot = runner.GetBlockSlot(actualHead);
            if (actualHead != expectedRoot)
                Assert.Fail($"step {stepIndex}: checks.head.root expected {expectedRoot}, actual {actualHead}");
            if (actualSlot != expectedSlot)
                Assert.Fail($"step {stepIndex}: checks.head.slot expected {expectedSlot}, actual {(actualSlot.HasValue ? actualSlot.Value.ToString() : "null")}");
        }

        if (TryGetChild(checks, "justified_checkpoint", out YamlNode? justifiedNode))
            AssertCheckpoint("justified_checkpoint", (YamlMappingNode)justifiedNode!, runner.JustifiedCheckpoint, stepIndex);

        if (TryGetChild(checks, "finalized_checkpoint", out YamlNode? finalizedNode))
            AssertCheckpoint("finalized_checkpoint", (YamlMappingNode)finalizedNode!, runner.FinalizedCheckpoint, stepIndex);

        if (TryGetScalar(checks, "proposer_boost_root", out string? boostRoot))
        {
            Hash256 expected = new(boostRoot!);
            if (runner.ProposerBoostRoot != expected)
                Assert.Fail($"step {stepIndex}: checks.proposer_boost_root expected {expected}, actual {runner.ProposerBoostRoot}");
        }
    }

    internal static void AssertCheckpoint(string name, YamlMappingNode node, CheckpointRef actual, int stepIndex)
    {
        ulong expectedEpoch = ulong.Parse(GetScalar(node, "epoch"));
        Hash256 expectedRoot = new(GetScalar(node, "root"));
        if (actual.Epoch != expectedEpoch)
            Assert.Fail($"step {stepIndex}: checks.{name}.epoch expected {expectedEpoch}, actual {actual.Epoch}");
        if (actual.Root != expectedRoot)
            Assert.Fail($"step {stepIndex}: checks.{name}.root expected {expectedRoot}, actual {actual.Root}");
    }

    // --- Minimal, dependency-free YAML mapping helpers (mirrors FuluDriverSupport.ParseFlowMap's
    // approach of comparing scalar key text directly, rather than trusting YamlNode's dictionary
    // equality semantics for a synthesized lookup key). ---

    internal static bool TryGetChild(YamlMappingNode map, string key, out YamlNode? value)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> kv in map.Children)
        {
            if (kv.Key is YamlScalarNode keyNode && keyNode.Value == key)
            {
                value = kv.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static bool HasKey(YamlMappingNode map, string key) => TryGetChild(map, key, out _);

    internal static bool TryGetScalar(YamlMappingNode map, string key, out string? value)
    {
        if (TryGetChild(map, key, out YamlNode? node) && node is YamlScalarNode scalar)
        {
            value = scalar.Value;
            return true;
        }
        value = null;
        return false;
    }

    internal static string GetScalar(YamlMappingNode map, string key) =>
        TryGetScalar(map, key, out string? value) ? value! : throw new KeyNotFoundException($"missing yaml key '{key}'");

    internal static bool GetBool(YamlMappingNode map, string key, bool defaultValue) =>
        TryGetScalar(map, key, out string? value) ? bool.Parse(value!) : defaultValue;

    internal static IEnumerable<string> Keys(YamlMappingNode map)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> kv in map.Children)
        {
            if (kv.Key is YamlScalarNode keyNode)
                yield return keyNode.Value ?? "";
        }
    }
}
