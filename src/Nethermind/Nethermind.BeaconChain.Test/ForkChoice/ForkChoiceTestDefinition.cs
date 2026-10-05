// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.ForkChoice.ProtoArrayTestBlocks;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public static class TestHashes
{
    public static Hash256 FromFirstByte(byte marker)
    {
        byte[] bytes = new byte[Hash256.Size];
        bytes[0] = marker;
        return new Hash256(bytes);
    }

    /// <summary>Write big-endian into the last eight bytes so numeric and lexicographic root order agree.</summary>
    public static Hash256 FromLow(ulong n)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), n);
        return new Hash256(bytes);
    }

    public static Hash256 GetRoot(ulong i) => FromLow(i + 1);

    public static CheckpointRef GetCheckpoint(ulong i) => new(i, GetRoot(i));
}

internal static class ProtoArrayTestBlocks
{
    internal static ProtoBlock CreateProtoBlock(
        ulong slot, Hash256 root, Hash256? parent, CheckpointRef justified, CheckpointRef finalized,
        ExecutionStatus executionStatus, Hash256 executionBlockHash,
        CheckpointRef? unrealizedJustified = null, CheckpointRef? unrealizedFinalized = null,
        bool isGloas = false, Hash256? parentBlockHash = null) => new(
            Slot: slot,
            Root: root,
            ParentRoot: parent,
            StateRoot: Hash256.Zero,
            JustifiedCheckpoint: justified,
            FinalizedCheckpoint: finalized,
            ExecutionStatus: executionStatus,
            ExecutionBlockHash: executionBlockHash,
            UnrealizedJustifiedCheckpoint: unrealizedJustified,
            UnrealizedFinalizedCheckpoint: unrealizedFinalized,
            IsGloas: isGloas,
            ParentBlockHash: parentBlockHash);
}

public abstract record Operation;

public sealed record FindHead(
    CheckpointRef JustifiedCheckpoint,
    CheckpointRef FinalizedCheckpoint,
    ulong[] JustifiedStateBalances,
    Hash256 ExpectedHead,
    Hash256? ProposerBoostRoot = null) : Operation;

public sealed record InvalidFindHead(
    CheckpointRef JustifiedCheckpoint,
    CheckpointRef FinalizedCheckpoint,
    ulong[] JustifiedStateBalances) : Operation;

public sealed record ProcessBlock(
    ulong Slot,
    Hash256 Root,
    Hash256 ParentRoot,
    CheckpointRef JustifiedCheckpoint,
    CheckpointRef FinalizedCheckpoint) : Operation;

public sealed record ProcessAttestation(ulong ValidatorIndex, Hash256 BlockRoot, ulong TargetEpoch) : Operation;

public sealed record Prune(Hash256 FinalizedRoot, int PruneThreshold, int ExpectedLength) : Operation;

public sealed record InvalidatePayload(Hash256 HeadBlockRoot, Hash256? LatestValidAncestorHash) : Operation;

public sealed record AssertWeight(Hash256 BlockRoot, ulong Weight) : Operation;

public sealed class ForkChoiceTestDefinition
{
    /// <summary>Lighthouse vectors use proposer_score_boost = 50.</summary>
    private const ulong ProposerScoreBoostPercent = 50;

    public required ulong FinalizedBlockSlot { get; init; }
    public required CheckpointRef JustifiedCheckpoint { get; init; }
    public required CheckpointRef FinalizedCheckpoint { get; init; }
    public required IReadOnlyList<Operation> Operations { get; init; }

    internal static ForkChoiceTestDefinition Create(CheckpointRef anchor, IReadOnlyList<Operation> operations) => new()
    {
        FinalizedBlockSlot = 0,
        JustifiedCheckpoint = anchor,
        FinalizedCheckpoint = anchor,
        Operations = operations,
    };

    public void Run()
    {
        ProtoArrayForkChoice forkChoice = new(
            currentSlot: FinalizedBlockSlot,
            finalizedBlockSlot: FinalizedBlockSlot,
            finalizedBlockStateRoot: Hash256.Zero,
            justifiedCheckpoint: JustifiedCheckpoint,
            finalizedCheckpoint: FinalizedCheckpoint,
            executionStatus: ExecutionStatus.Optimistic,
            executionBlockHash: Hash256.Zero,
            proposerScoreBoostPercent: ProposerScoreBoostPercent);

        for (int opIndex = 0; opIndex < Operations.Count; opIndex++)
        {
            Operation op = Operations[opIndex];
            string opDescription = $"operation {opIndex}: {op}";

            switch (op)
            {
                case FindHead findHead:
                    if (findHead.ProposerBoostRoot is { } proposerBoostRoot) forkChoice.SetProposerBoostRoot(proposerBoostRoot);
                    else forkChoice.ResetProposerBoostRoot();

                    Hash256 head = forkChoice.GetHead(
                        findHead.JustifiedCheckpoint,
                        findHead.FinalizedCheckpoint,
                        JustifiedBalances.FromEffectiveBalances(findHead.JustifiedStateBalances),
                        equivocatingIndices: null,
                        currentSlot: 0);
                    Assert.That(head, Is.EqualTo(findHead.ExpectedHead), opDescription);
                    break;

                case InvalidFindHead invalidFindHead:
                    forkChoice.ResetProposerBoostRoot();
                    Assert.That(
                        () => forkChoice.GetHead(
                            invalidFindHead.JustifiedCheckpoint,
                            invalidFindHead.FinalizedCheckpoint,
                            JustifiedBalances.FromEffectiveBalances(invalidFindHead.JustifiedStateBalances),
                            equivocatingIndices: null,
                            currentSlot: 0),
                        Throws.TypeOf<ProtoArrayException>(),
                        opDescription);
                    break;

                case ProcessBlock processBlock:
                    ProtoBlock block = CreateProtoBlock(processBlock.Slot, processBlock.Root, processBlock.ParentRoot,
                        processBlock.JustifiedCheckpoint, processBlock.FinalizedCheckpoint, ExecutionStatus.Optimistic, processBlock.Root);
                    forkChoice.ProcessBlock(block, processBlock.Slot, JustifiedCheckpoint, FinalizedCheckpoint);
                    break;

                case ProcessAttestation processAttestation:
                    forkChoice.ProcessAttestation(processAttestation.ValidatorIndex, processAttestation.BlockRoot, processAttestation.TargetEpoch);
                    break;

                case Prune prune:
                    forkChoice.PruneThreshold = prune.PruneThreshold;
                    forkChoice.MaybePrune(prune.FinalizedRoot);
                    Assert.That(forkChoice.Count, Is.EqualTo(prune.ExpectedLength), opDescription);
                    break;

                case InvalidatePayload invalidatePayload:
                    InvalidationOperation invalidation = invalidatePayload.LatestValidAncestorHash is { } latestValidAncestor
                        ? InvalidationOperation.InvalidateMany(invalidatePayload.HeadBlockRoot, alwaysInvalidateHead: true, latestValidAncestor)
                        : InvalidationOperation.InvalidateOne(invalidatePayload.HeadBlockRoot);
                    forkChoice.ProcessExecutionPayloadInvalidation(invalidation, FinalizedCheckpoint);
                    break;

                case AssertWeight assertWeight:
                    Assert.That(forkChoice.GetWeight(assertWeight.BlockRoot), Is.EqualTo(assertWeight.Weight), opDescription);
                    break;

                default:
                    Assert.Fail($"unhandled {opDescription}");
                    break;
            }
        }
    }
}
