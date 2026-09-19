// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>A node in the LMD-GHOST proto-array, ported from Lighthouse's <c>ProtoNode</c>.</summary>
public sealed class ProtoNode
{
    /// <summary>Not used by the proto-array itself; kept so upstream fork-choice logic can query block slots.</summary>
    public required ulong Slot { get; init; }

    /// <summary>Not used by the proto-array itself; kept for upstream components (attestation verification).</summary>
    public required Hash256 StateRoot { get; init; }

    /// <summary>The root used as <c>attestation.data.target.root</c> for an LMD vote cast for this block.</summary>
    public required Hash256 TargetRoot { get; init; }

    public required Hash256 Root { get; init; }

    /// <summary>Index of the parent in <see cref="ProtoArray.Nodes"/>, or <c>null</c> for tree roots.</summary>
    public int? Parent { get; set; }

    public required CheckpointRef JustifiedCheckpoint { get; init; }

    public required CheckpointRef FinalizedCheckpoint { get; init; }

    /// <summary>Accumulated attestation weight in Gwei of this node and all its descendants.</summary>
    public ulong Weight { get; set; }

    public int? BestChild { get; set; }

    public int? BestDescendant { get; set; }

    public ExecutionStatus ExecutionStatus { get; set; }

    /// <summary>The execution payload block hash; <c>null</c> if and only if <see cref="ExecutionStatus"/> is <see cref="ExecutionStatus.Irrelevant"/>.</summary>
    public Hash256? ExecutionBlockHash { get; init; }

    public CheckpointRef? UnrealizedJustifiedCheckpoint { get; init; }

    public CheckpointRef? UnrealizedFinalizedCheckpoint { get; init; }
}

/// <summary>Block information to be applied to the fork choice; a simplified beacon block (Lighthouse's <c>Block</c>).</summary>
/// <remarks>
/// The unrealized checkpoints are computed by the state-transition layer and passed in; the
/// proto-array only stores them for viability ("pull-up") decisions.
/// </remarks>
public sealed record ProtoBlock(
    ulong Slot,
    Hash256 Root,
    Hash256? ParentRoot,
    Hash256 StateRoot,
    Hash256 TargetRoot,
    CheckpointRef JustifiedCheckpoint,
    CheckpointRef FinalizedCheckpoint,
    ExecutionStatus ExecutionStatus,
    Hash256? ExecutionBlockHash,
    CheckpointRef? UnrealizedJustifiedCheckpoint,
    CheckpointRef? UnrealizedFinalizedCheckpoint);
