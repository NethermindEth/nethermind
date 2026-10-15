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
    private readonly DeferredHeadState? _state;

    /// <summary>The head's post-state for the Beacon API once it is taken, otherwise <c>null</c>; reading it never waits or starts the copy.</summary>
    internal ForkedBeaconState? State
    {
        get => _state?.Taken;
        init => _state = value is null ? null : new DeferredHeadState(value);
    }

    /// <summary>The head's post-state, taken only when a reader asks for it.</summary>
    internal DeferredHeadState? DeferredState
    {
        get => _state;
        init => _state = value;
    }
}

/// <summary>The post-state of one head for the Beacon API, taken once by the first reader that asks for it and shared by every later one.</summary>
/// <remarks>A take that ends with no state or a fault is not kept, so the next reader takes it again.</remarks>
internal sealed class DeferredHeadState
{
    private readonly Func<Task<ForkedBeaconState?>>? _take;
    private readonly Lock _lock = new();
    private Task<ForkedBeaconState?>? _taken;

    /// <param name="take">Takes the state; it must not block the calling thread.</param>
    public DeferredHeadState(Func<Task<ForkedBeaconState?>> take) => _take = take;

    /// <summary>A state already taken.</summary>
    public DeferredHeadState(ForkedBeaconState state) => _taken = Task.FromResult<ForkedBeaconState?>(state);

    /// <summary>The state if a take has ended with it, otherwise <c>null</c>.</summary>
    public ForkedBeaconState? Taken => Volatile.Read(ref _taken) is { IsCompletedSuccessfully: true } taken ? taken.Result : null;

    /// <summary>Starts the take unless one is running or has ended with the state.</summary>
    public Task<ForkedBeaconState?> TakeAsync()
    {
        lock (_lock)
        {
            if (_take is not null && (_taken is null || _taken is { IsCompleted: true } ended && (!ended.IsCompletedSuccessfully || ended.Result is null)))
            {
                _taken = _take();
            }

            return _taken!;
        }
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
