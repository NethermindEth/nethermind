// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>A node in the LMD-GHOST proto-array, ported from Lighthouse's <c>ProtoNode</c>.</summary>
public sealed class ProtoNode
{
    /// <summary>Not used by the proto-array itself; kept so upstream fork-choice logic can query block slots.</summary>
    public required ulong Slot { get; init; }

    /// <summary>Not used by the proto-array itself; kept for upstream components (attestation verification).</summary>
    public required Hash256 StateRoot { get; init; }

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

    /// <summary>Whether the block carries a signed execution payload bid (a Gloas block).</summary>
    public bool IsGloas { get; init; }

    /// <summary>The spec's <c>get_parent_payload_status</c>: which node of the parent this block builds on.</summary>
    /// <remarks>
    /// specs/gloas/fork-choice.md: <see cref="ForkChoicePayloadStatus.Full"/> when the bid's <c>parent_block_hash</c> equals the
    /// parent bid's <c>block_hash</c>. A pre-Gloas parent has no bid; its payload came inside the block, so its
    /// children build on it full. A tree root has no parent and keeps <see cref="ForkChoicePayloadStatus.Full"/>.
    /// </remarks>
    public ForkChoicePayloadStatus ParentPayloadStatus { get; init; } = ForkChoicePayloadStatus.Full;

    /// <summary>Weight in Gwei of the (<see cref="Root"/>, EMPTY) node: votes for it plus the weight of every child built on the empty parent.</summary>
    public ulong EmptyWeight { get; set; }

    /// <summary>Weight in Gwei of the (<see cref="Root"/>, FULL) node: votes for it plus the weight of every child built on the full parent.</summary>
    public ulong FullWeight { get; set; }

    /// <summary>Indices in <see cref="ProtoArray.Nodes"/> of the direct children, in insertion order.</summary>
    public List<int> Children { get; } = [];
}

/// <summary>Block information to be applied to the fork choice; a simplified beacon block (Lighthouse's <c>Block</c>).</summary>
/// <remarks>
/// The unrealized checkpoints are computed by the state-transition layer and passed in; the
/// proto-array only stores them for viability ("pull-up") decisions. A Gloas block sets <c>IsGloas</c> and
/// passes its bid's <c>parent_block_hash</c> as <c>ParentBlockHash</c>, from which the proto-array derives
/// <see cref="ProtoNode.ParentPayloadStatus"/>.
/// </remarks>
public sealed record ProtoBlock(
    ulong Slot,
    Hash256 Root,
    Hash256? ParentRoot,
    Hash256 StateRoot,
    CheckpointRef JustifiedCheckpoint,
    CheckpointRef FinalizedCheckpoint,
    ExecutionStatus ExecutionStatus,
    Hash256? ExecutionBlockHash,
    CheckpointRef? UnrealizedJustifiedCheckpoint,
    CheckpointRef? UnrealizedFinalizedCheckpoint,
    bool IsGloas = false,
    Hash256? ParentBlockHash = null);
