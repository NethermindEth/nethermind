// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

/// <remarks>
/// Pyspec's optimistic add_block replays body votes/slashings before invalidating an EL-INVALID block;
/// a refused fork_choice block replays nothing. Ancestors are invalidated back to latest_valid_hash.
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

    internal sealed class FixedNewPayloadNotifier(ExecutionStatus status) : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => status;
    }

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

    public static ForkChoiceRunner Run(string casePath) => Run(casePath, out _);
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

        Nethermind.BeaconChain.Spec.BeaconChainSpec spec = FuluDriverSupport.CaseSpec(casePath);
        ForkChoiceRunner runner = new(spec, anchorState, anchorBlock, stateProvider, pubkeys);
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
            RunOperandStep<Attestation>($"step {stepIndex}: attestation {attestationKey}", GetBool(step, "valid", defaultValue: true),
                SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, attestationKey + ".ssz_snappy")), operand => runner.OnAttestation(operand, isFromBlock: false, verifySignature: true));
            return;
        }

        if (TryGetScalar(step, "attester_slashing", out string? slashingKey))
        {
            RunOperandStep<AttesterSlashing>($"step {stepIndex}: attester_slashing {slashingKey}", GetBool(step, "valid", defaultValue: true),
                SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, slashingKey + ".ssz_snappy")), operand => runner.OnAttesterSlashing(operand, verifySignatures: true));
            return;
        }

        if (TryGetChild(step, "checks", out YamlNode? checksNode))
        {
            RunChecksStep((YamlMappingNode)checksNode!, runner, stepIndex);
            return;
        }

        throw new NotImplementedInDriverException($"step {stepIndex}: unrecognized step shape '{string.Join(",", Keys(step))}' has no entry point in this driver.");
    }

    /// <summary>Transitions a clone of the parent state with a fresh EpochCache because its balance memo is not fork-aware.</summary>
    private static Exception? RunBlockStep(string casePath, string blockKey, bool expectedValid, ForkChoiceRunner runner, InMemoryStateProvider stateProvider, PubkeyCache pubkeys, int stepIndex, DataColumnSidecar[]? dataColumns, bool executionValid, Dictionary<Hash256, PayloadInfo> payloadInfos)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, blockKey + ".ssz_snappy"));
        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock signedBlock);
        BeaconBlock block = signedBlock.Message!;
        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        PayloadInfo? payloadInfo = payloadInfos.GetValueOrDefault(block.Body!.ExecutionPayload!.BlockHash!);
        ExecutionStatus executionStatus = payloadInfo?.ExecutionStatus ?? (executionValid ? ExecutionStatus.Valid : ExecutionStatus.Invalid);

        BeaconStateFulu? postState = null;
        Exception? rejection = Attempt(() =>
        {
            // on_block asserts the parent is known before any state transition, so a block with no parent
            // state goes to fork choice with the anchor state standing in for the post-state it cannot have.
            if (stateProvider.States.TryGetValue(block.ParentRoot!, out BeaconStateFulu? parentState))
            {
                postState = parentState.Clone();
                StateTransition.Apply(postState, signedBlock, new EpochCache(), pubkeys, new FixedNewPayloadNotifier(executionStatus), FuluDriverSupport.CaseSpec(casePath), validateResult: true, verifySignatures: true);
            }

            runner.OnBlock(signedBlock, postState ?? stateProvider.Anchor, payloadInfo is null ? ExecutionStatus.Valid : executionStatus, dataColumns);
        });

        AssertVerdict($"step {stepIndex}: block {blockKey} (slot {block.Slot})", expectedValid, rejection);

        if (payloadInfo is { IsInvalid: true })
            InvalidateBackToLatestValidHash(runner, block.ParentRoot!, payloadInfo.LatestValidHash);
        else if (rejection is not null)
            return rejection;
        else
            stateProvider.States[blockRoot] = postState ?? throw new InvalidOperationException($"step {stepIndex}: block {blockKey} was accepted without a parent state");

        // The fork_choice and sync test formats treat an on_block step as implying on_attestation(is_from_block)
        // for every body attestation and on_attester_slashing for every body slashing.
        ReplayBodyOperands(block.Body!.Attestations!, operand => runner.OnBodyAttestation(operand, blockRoot),
            i => $"step {stepIndex}: body attestation {i} of block {blockKey} rejected");

        // A vote waiting for a build would reach the store only on a later tick, after the vector's checks of this step.
        Assert.That(runner.DeferredBodyVoteCount, Is.Zero, $"step {stepIndex}: block {blockKey} left body attestations waiting for a target state build");

        ReplayBodyOperands(block.Body!.AttesterSlashings!, operand => runner.OnAttesterSlashing(operand, verifySignatures: false),
            i => $"step {stepIndex}: body attester slashing {i} of block {blockKey} rejected");

        return rejection;
    }

    /// <summary>Invalidates payload ancestors up to, but excluding, <paramref name="latestValidHash"/>, matching pyspec add_optimistic_block.</summary>
    private static void InvalidateBackToLatestValidHash(ForkChoiceRunner runner, Hash256 parentRoot, Hash256? latestValidHash)
    {
        if (latestValidHash is not null && runner.ContainsBlock(parentRoot) && runner.GetExecutionBlockHash(parentRoot) != latestValidHash)
            runner.OnInvalidExecutionPayload(parentRoot, latestValidHash);
    }

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

    internal static void ReplayBodyOperands<T>(T[] operands, Action<T> apply, Func<int, string> rejectionSubject)
    {
        for (int i = 0; i < operands.Length; i++)
        {
            T operand = operands[i];
            if (Attempt(() => apply(operand)) is { } ex)
                Assert.Fail($"{rejectionSubject(i)}: {ex.Message}");
        }
    }

    internal static void RunOperandStep<T>(string subject, bool expectedValid, byte[] ssz, Action<T> apply) where T : ISszCodec<T>
    {
        T.Decode(ssz, out T operand);
        AssertVerdict(subject, expectedValid, Attempt(() => apply(operand)));
    }

    internal static Exception? Attempt(Action action)
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
    internal static void AssertVerdict(string subject, bool expectedValid, Exception? rejection)
    {
        if (rejection is null && !expectedValid)
            Assert.Fail($"{subject} was expected to be REJECTED but the driver accepted it");
        if (rejection is not null && expectedValid)
            Assert.Fail($"{subject} was expected to be accepted but the driver rejected it: {rejection.Message}");
        if (rejection is not null && !FuluDriverSupport.IsSpecRejection(rejection))
            Assert.Fail($"{subject} was rejected by {rejection.GetType().Name} rather than a spec assertion: {rejection}");
    }

    private static void RunChecksStep(YamlMappingNode checks, ForkChoiceRunner runner, int stepIndex)
    {
        if (TryGetScalar(checks, "get_proposer_head", out string? proposerHeadRoot))
            AssertEqual(stepIndex, "get_proposer_head", new Hash256(proposerHeadRoot!), runner.GetProposerHead(runner.GetHead(), runner.CurrentSlot));

        if (TryGetScalar(checks, "time", out string? time))
            AssertEqual(stepIndex, "time", ulong.Parse(time!), runner.Time - runner.GenesisTime);

        if (TryGetScalar(checks, "genesis_time", out string? genesisTime))
            AssertEqual(stepIndex, "genesis_time", ulong.Parse(genesisTime!), runner.GenesisTime);

        if (TryGetChild(checks, "head", out YamlNode? headNode))
        {
            YamlMappingNode head = (YamlMappingNode)headNode!;
            Hash256 expectedRoot = new(GetScalar(head, "root"));
            ulong expectedSlot = ulong.Parse(GetScalar(head, "slot"));
            Hash256 actualHead = runner.GetHead();
            ulong? actualSlot = runner.GetBlockSlot(actualHead);
            AssertEqual(stepIndex, "head.root", expectedRoot, actualHead);
            if (actualSlot != expectedSlot)
                Assert.Fail($"step {stepIndex}: checks.head.slot expected {expectedSlot}, actual {(actualSlot.HasValue ? actualSlot.Value.ToString() : "null")}");
        }

        if (TryGetChild(checks, "justified_checkpoint", out YamlNode? justifiedNode))
            AssertCheckpoint("justified_checkpoint", (YamlMappingNode)justifiedNode!, runner.JustifiedCheckpoint, stepIndex);

        if (TryGetChild(checks, "finalized_checkpoint", out YamlNode? finalizedNode))
            AssertCheckpoint("finalized_checkpoint", (YamlMappingNode)finalizedNode!, runner.FinalizedCheckpoint, stepIndex);

        if (TryGetScalar(checks, "proposer_boost_root", out string? boostRoot))
            AssertEqual(stepIndex, "proposer_boost_root", new Hash256(boostRoot!), runner.ProposerBoostRoot);
    }

    internal static void AssertCheckpoint(string name, YamlMappingNode node, CheckpointRef actual, int stepIndex)
    {
        ulong expectedEpoch = ulong.Parse(GetScalar(node, "epoch"));
        Hash256 expectedRoot = new(GetScalar(node, "root"));
        AssertEqual(stepIndex, $"{name}.epoch", expectedEpoch, actual.Epoch);
        AssertEqual(stepIndex, $"{name}.root", expectedRoot, actual.Root);
    }

    internal static void AssertEqual<T>(int stepIndex, string check, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            Assert.Fail($"step {stepIndex}: checks.{check} expected {expected}, actual {actual}");
    }

    // Compare scalar keys directly; synthesized YamlNode keys need not compare equal.

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
