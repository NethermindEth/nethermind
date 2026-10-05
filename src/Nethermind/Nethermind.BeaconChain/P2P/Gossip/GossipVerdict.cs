// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>The pending pubsub verdict of one gossip message whose validation the router deferred; it is given at most once.</summary>
/// <remarks>
/// <see cref="Complete"/> hands the verdict to <see cref="PubsubRouter.CompleteValidation"/>, which forwards an
/// <see cref="MessageValidity.Accepted"/> message and charges the peer that delivered a <see cref="MessageValidity.Rejected"/> one,
/// and only then ends <see cref="Completion"/>: the router drops a pending message once the task validating it ends.
/// </remarks>
public sealed class GossipVerdict
{
    private const int Pending = 0;
    private const int Given = 1;
    private const int Abandoned = 2;

    /// <summary>The verdict of a message no pubsub router waits on, such as one handed to a router directly; giving it does nothing.</summary>
    public static readonly GossipVerdict None = new(null, null);

    private readonly Func<MessageValidity, bool>? _complete;
    private readonly Action? _onEnd;
    private readonly bool _local;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;
    private int _handedOff;
    private Action? _onThrottled;

    /// <param name="complete">Gives the router a verdict for the deferred message and returns whether the router applied it.</param>
    /// <param name="onEnd">Runs once, when the verdict is given or abandoned.</param>
    internal GossipVerdict(Func<MessageValidity, bool>? complete, Action? onEnd)
        : this(complete, onEnd, local: false)
    {
    }

    private GossipVerdict(Func<MessageValidity, bool>? complete, Action? onEnd, bool local)
    {
        _complete = complete;
        _onEnd = onEnd;
        _local = local;
    }

    /// <summary>A verdict no router waits on that still runs <see cref="ReleaseOnThrottle"/>, for a message whose router verdict was given already.</summary>
    internal static GossipVerdict Local() => new(static _ => true, null, local: true);

    /// <summary>Whether the router's verdict for the message was given already, so a consumer runs no gossip rule for it.</summary>
    internal bool IsLocal => _local;

    /// <summary>Ends once the verdict is given or abandoned.</summary>
    internal Task Completion => _completion.Task;

    /// <summary>Whether the verdict was given or abandoned.</summary>
    public bool IsCompleted => Volatile.Read(ref _state) != Pending;

    /// <summary>Whether a consumer took the verdict over, so the code that handed it the message must not give one; once taken over it stays so.</summary>
    internal bool IsHandedOff => Volatile.Read(ref _handedOff) != 0;

    /// <summary>Runs <paramref name="release"/> if the verdict given is <see cref="MessageValidity.Throttled"/>: refused for local load, so a later copy must be checked again.</summary>
    /// <remarks>Register before the verdict is handed off; a consumer may give it at once.</remarks>
    internal void ReleaseOnThrottle(Action release)
    {
        if (_complete is not null)
        {
            _onThrottled += release;
        }
    }

    /// <summary>Records that a consumer took the verdict over.</summary>
    internal void HandOff()
    {
        if (_complete is not null)
        {
            Volatile.Write(ref _handedOff, 1);
        }
    }

    /// <summary>Gives the router <paramref name="validity"/> for the message, unless a verdict was given or abandoned already.</summary>
    /// <returns>Whether the router applied it; it does not once the message expired from its pending set.</returns>
    public bool Complete(MessageValidity validity)
    {
        if (_complete is null)
        {
            return false;
        }

        int previous = Interlocked.CompareExchange(ref _state, Given, Pending);
        if (previous != Pending)
        {
            if (previous == Abandoned)
            {
                Interlocked.Increment(ref Metrics.GossipLateVerdictCount);
            }

            return false;
        }

        if (validity == MessageValidity.Throttled)
        {
            _onThrottled?.Invoke();
        }

        try
        {
            bool applied = _complete(validity);
            if (_local)
            {
                return applied;
            }

            if (!applied && validity != MessageValidity.Throttled)
            {
                // The router expired the message before the import pipeline gave its verdict.
                Interlocked.Increment(ref Metrics.GossipLateVerdictCount);
            }
            else if (applied && validity == MessageValidity.Accepted)
            {
                Interlocked.Increment(ref Metrics.GossipForwardedCount);
            }

            return applied;
        }
        finally
        {
            End();
        }
    }

    /// <summary>Gives up on the verdict without telling the router, which then drops the message without caching its id.</summary>
    internal void Abandon()
    {
        if (_complete is not null && Interlocked.CompareExchange(ref _state, Abandoned, Pending) == Pending)
        {
            Interlocked.Increment(ref Metrics.GossipAbandonedVerdictCount);
            End();
        }
    }

    private void End()
    {
        _onEnd?.Invoke();
        _completion.TrySetResult();
    }
}
