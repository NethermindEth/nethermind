// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.ForkChoice;

namespace Nethermind.BeaconChain.Sync;

/// <summary>A head computation published as one value (fork-choice.md get_head).</summary>
/// <param name="Status">The status, never mutated after publication.</param>
/// <param name="FullHeadRoot">The FULL head root, otherwise null (gloas/fork-choice.md get_head).</param>
/// <param name="JustifiedRoot">The head computation's justified checkpoint root.</param>
/// <param name="ExecutionInSync">Whether the execution layer confirmed the head VALID.</param>
public sealed record HeadSnapshot(StatusMessageV2 Status, Hash256? FullHeadRoot, Hash256 JustifiedRoot, bool ExecutionInSync)
{
    /// <summary>The head's post-state for the Beacon API, or <c>null</c> when it was not captured.</summary>
    internal ForkedBeaconState? State { get; init; }

    internal CheckpointStateSnapshot? JustifiedState { get; init; }
    internal CheckpointStateSnapshot? FinalizedState { get; init; }

    /// <summary>The captured post-state of <paramref name="blockRoot"/> when it is the head, otherwise <c>null</c>.</summary>
    internal ForkedBeaconState? GetState(Hash256 blockRoot) =>
        blockRoot == Status.HeadRoot ? State : null;
}

/// <summary>A checkpoint's epoch-start state, shared unchanged across head publications.</summary>
internal sealed record CheckpointStateSnapshot(CheckpointRef Checkpoint, ForkedBeaconState State, Hash256 StateRoot);

/// <summary>Publishes one completed get_head view to other threads (fork-choice.md).</summary>
/// <remarks><see cref="Current"/> stays <c>null</c> until the orchestrator is initialized.</remarks>
public sealed class HeadSnapshotHolder
{
    private HeadSnapshot? _current;
    internal bool CaptureState { get; set; }

    public HeadSnapshot? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
