// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.BeaconChain.StateTransition;

namespace Nethermind.BeaconChain.Sync;

/// <summary>A head computation published as one value (fork-choice.md get_head).</summary>
/// <param name="Status">The status, never mutated after publication.</param>
/// <param name="FullHeadRoot">The FULL head root, otherwise null (gloas/fork-choice.md get_head).</param>
/// <param name="JustifiedRoot">The head computation's justified checkpoint root.</param>
/// <param name="ExecutionInSync">Whether the execution layer confirmed the head VALID.</param>
public sealed record HeadSnapshot(StatusMessageV2 Status, Hash256? FullHeadRoot, Hash256 JustifiedRoot, bool ExecutionInSync)
{
    private readonly Lazy<ForkedBeaconState?>? _state;

    /// <summary>The head's post-state for the Beacon API; a deferred one is taken on the first read and shared by every later one.</summary>
    internal ForkedBeaconState? State
    {
        get => _state?.Value;
        init => _state = value is null ? null : new Lazy<ForkedBeaconState?>(value);
    }

    /// <summary>The head's post-state, taken only when <see cref="State"/> is first read.</summary>
    internal Lazy<ForkedBeaconState?>? DeferredState
    {
        get => _state;
        init => _state = value;
    }
}

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
