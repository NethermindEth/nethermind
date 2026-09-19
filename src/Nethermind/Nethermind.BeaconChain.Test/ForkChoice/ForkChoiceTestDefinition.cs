// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>Deterministic test hashes, mirroring the helpers of Lighthouse's <c>fork_choice_test_definition</c>.</summary>
public static class TestHashes
{
    /// <summary>A hash with <paramref name="n"/> written big-endian into its last 8 bytes (Lighthouse's <c>Hash256::from_low_u64_be</c>).</summary>
    /// <remarks>
    /// Only internal consistency matters, but the big-endian layout also preserves numeric order
    /// under the lexicographic root comparison used by the equal-weight tie-break.
    /// </remarks>
    public static Hash256 FromLow(ulong n)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), n);
        return new Hash256(bytes);
    }

    /// <summary>A root that is not the zero hash; Lighthouse's <c>get_root</c>. Also used for execution block hashes (<c>get_hash</c>).</summary>
    public static Hash256 GetRoot(ulong i) => FromLow(i + 1);

    /// <summary>A checkpoint at epoch <paramref name="i"/> with root <see cref="GetRoot"/>(<paramref name="i"/>); Lighthouse's <c>get_checkpoint</c>.</summary>
    public static CheckpointRef GetCheckpoint(ulong i) => new(i, GetRoot(i));
}

public abstract record Operation;

/// <summary>Runs <c>GetHead</c> and asserts the returned head; carries an optional proposer boost root (Lighthouse's <c>ProposerBoostFindHead</c>).</summary>
public sealed record FindHead(
    CheckpointRef JustifiedCheckpoint,
    CheckpointRef FinalizedCheckpoint,
    ulong[] JustifiedStateBalances,
    Hash256 ExpectedHead,
    Hash256? ProposerBoostRoot = null) : Operation;

/// <summary>Runs <c>GetHead</c> and asserts that it fails.</summary>
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

/// <summary>
/// An interpreter for Lighthouse's <c>ForkChoiceTestDefinition</c> operation sequences, driving a
/// <see cref="ProtoArrayForkChoice"/> and asserting after every step.
/// </summary>
public sealed class ForkChoiceTestDefinition
{
    /// <summary>Lighthouse runs these vectors with <c>proposer_score_boost = 50</c>.</summary>
    private const ulong ProposerScoreBoostPercent = 50;

    public required ulong FinalizedBlockSlot { get; init; }
    public required CheckpointRef JustifiedCheckpoint { get; init; }
    public required CheckpointRef FinalizedCheckpoint { get; init; }
    public required IReadOnlyList<Operation> Operations { get; init; }

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
                    // All blocks are imported optimistically with an execution hash equal to their
                    // root (Lighthouse's ExecutionBlockHash::from_root).
                    ProtoBlock block = new(
                        Slot: processBlock.Slot,
                        Root: processBlock.Root,
                        ParentRoot: processBlock.ParentRoot,
                        StateRoot: Hash256.Zero,
                        TargetRoot: Hash256.Zero,
                        JustifiedCheckpoint: processBlock.JustifiedCheckpoint,
                        FinalizedCheckpoint: processBlock.FinalizedCheckpoint,
                        ExecutionStatus: ExecutionStatus.Optimistic,
                        ExecutionBlockHash: processBlock.Root,
                        UnrealizedJustifiedCheckpoint: null,
                        UnrealizedFinalizedCheckpoint: null);
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
