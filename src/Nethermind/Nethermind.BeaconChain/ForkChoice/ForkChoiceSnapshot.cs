// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>
/// An immutable copy of the fork-choice store as of one head computation: the checkpoints, the
/// proposer boost root and every proto-array node in proto-array order (parents before children).
/// </summary>
/// <remarks>
/// Taken by <see cref="ForkChoiceRunner.Snapshot"/> on the import worker and read from any thread
/// through <see cref="ForkChoiceSnapshotHolder"/>; the copy is what makes that safe, since the live
/// proto-array is not thread-safe. Node weights are as settled by the <c>get_head</c> that preceded
/// the copy.
/// </remarks>
public sealed record ForkChoiceSnapshot(
    CheckpointRef JustifiedCheckpoint,
    CheckpointRef FinalizedCheckpoint,
    Hash256 ProposerBoostRoot,
    IReadOnlyList<ForkChoiceSnapshotNode> Nodes);

/// <summary>One proto-array node, with the parent index already resolved to a root.</summary>
/// <param name="ParentRoot"><c>null</c> for the tree root, whose parent is the anchor's or was pruned.</param>
/// <param name="ExecutionBlockHash"><c>null</c> only for a payload-less block (<see cref="ExecutionStatus.Irrelevant"/>).</param>
public sealed record ForkChoiceSnapshotNode(
    ulong Slot,
    Hash256 Root,
    Hash256? ParentRoot,
    ulong JustifiedEpoch,
    ulong FinalizedEpoch,
    ulong Weight,
    ExecutionStatus ExecutionStatus,
    Hash256? ExecutionBlockHash);

/// <summary>
/// Hands the importer's latest <see cref="ForkChoiceSnapshot"/> to readers on other threads (the
/// beacon API) without either side holding a reference to the importer, which is created per sync
/// session and is not a container service.
/// </summary>
/// <remarks><see cref="Current"/> stays <c>null</c> until the first head computation.</remarks>
public sealed class ForkChoiceSnapshotHolder
{
    private ForkChoiceSnapshot? _current;

    public ForkChoiceSnapshot? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
