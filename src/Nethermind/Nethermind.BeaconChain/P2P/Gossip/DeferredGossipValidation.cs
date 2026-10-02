// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>Runs <see cref="GossipMessageValidator"/> off the pubsub router's monitor and gives the router each message's verdict once its checks finish.</summary>
/// <remarks>
/// <para>
/// The router calls <see cref="Verify"/> under its monitor for every new message id. It drops a message on a topic this node
/// does not handle there, and otherwise returns <see cref="MessageValidity.Deferred"/>. The router then calls
/// <see cref="ValidateAsync"/> outside its monitor on the delivering peer's read loop, so one peer's messages are checked in
/// order and a slow check delays only that peer. A message whose checks continue in the import pipeline keeps its
/// <see cref="GossipVerdict"/> there, and the router forwards it only once that verdict is <see cref="MessageValidity.Accepted"/>
/// (phase0 p2p-interface.md "Topics and messages": ACCEPT once every validation passed).
/// </para>
/// <para>
/// Past <paramref name="maxPending"/> messages or <paramref name="maxPendingBytes"/> bytes waiting for a verdict, or a fraction of the
/// messages from one delivering peer, a new message is <see cref="MessageValidity.Throttled"/>: the router caches no id for it, so a
/// later copy is checked, and charges its sender nothing. Aggregates and payload attestations are held to their own bound of
/// <paramref name="maxPendingVotes"/> instead, the import worker's vote queue, so their volume cannot take the room of a block.
/// A verdict not given within <paramref name="timeout"/> is abandoned, which the router treats the same way.
/// </para>
/// <para>
/// A message is reserved when it is deferred, so every message of one RPC is held to the bounds before the router dispatches any of them.
/// The router's own bounds sit above these, so it does not refuse a message deferred here; one it never dispatches, as one it skips once
/// expired, has its reservation released when the reservation expires.
/// </para>
/// </remarks>
internal sealed class DeferredGossipValidation(
    PubsubRouter router,
    GossipMessageValidator validator,
    int maxPending,
    long maxPendingBytes,
    int maxPendingVotes,
    TimeSpan timeout,
    ILogger logger,
    CancellationToken stopping,
    TimeProvider? time = null)
{
    /// <summary>The largest message counted as a vote; anything larger is no legal aggregate or payload attestation and is held to the main bounds.</summary>
    internal const int MaxVoteMessageBytes = 32 * 1024;

    // Past the router's own expiry of a message it has not dispatched: it skips that message, so the reservation is released here instead.
    private static readonly TimeSpan ReservationSlack = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Message, Reservation> _reservations = new(ReferenceEqualityComparer.Instance);
    private long _nextSweepTicks;
    private readonly int _maxPendingPerSource = Math.Max(1, maxPending / 8);
    private readonly ConcurrentDictionary<PeerId, int> _pendingBySource = new();
    private int _pending;
    private long _pendingBytes;
    private int _pendingVotes;

    /// <summary>The messages other than votes deferred for validation whose verdict is not given yet.</summary>
    internal int Pending => Volatile.Read(ref _pending);

    /// <summary>The bytes of <see cref="Pending"/>.</summary>
    internal long PendingBytes => Interlocked.Read(ref _pendingBytes);

    /// <summary>The votes deferred for validation whose verdict is not given yet.</summary>
    internal int PendingVotes => Volatile.Read(ref _pendingVotes);

    /// <summary>The most messages other than votes and columns from one delivering peer that may await a verdict at once.</summary>
    internal int MaxPendingPerSource => _maxPendingPerSource;

    /// <summary>The router's synchronous hook: drops a message on a topic this node does not handle, and reserves room for the others or throttles them.</summary>
    /// <remarks>It runs under the router's monitor, so reservations of concurrent RPCs do not race.</remarks>
    public MessageValidity Verify(PeerId source, Message message)
    {
        if (!validator.IsDeferred(message.Topic))
        {
            return validator.Validate(message, GossipVerdict.None);
        }

        ReleaseExpired();
        long size = message.CalculateSize();
        bool vote = IsVote(message, size);
        // A batched IWANT answer carries many columns from one peer; a column holds its reservation only until its checks return.
        bool sharedBySource = !vote && !GossipMessageValidator.IsColumn(message.Topic);
        if (vote ? PendingVotes >= maxPendingVotes
            : Pending >= maxPending || PendingBytes + size > maxPendingBytes || (sharedBySource && _pendingBySource.GetValueOrDefault(source) >= _maxPendingPerSource))
        {
            return Throttle(source, message);
        }

        Reservation reservation = new(this, source, vote ? 0 : size, vote, sharedBySource, _time.GetUtcNow() + timeout + ReservationSlack);
        if (vote)
        {
            Interlocked.Increment(ref _pendingVotes);
        }
        else
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Add(ref _pendingBytes, size);
            if (sharedBySource)
            {
                _pendingBySource.AddOrUpdate(source, 1, static (_, count) => count + 1);
            }
        }

        _reservations[message] = reservation;
        return MessageValidity.Deferred;
    }

    /// <summary>Releases the reservations of messages the router never dispatched, which it skips once they expire; at most once a second.</summary>
    private void ReleaseExpired()
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (now.UtcTicks < Volatile.Read(ref _nextSweepTicks) || _reservations.IsEmpty)
        {
            return;
        }

        Volatile.Write(ref _nextSweepTicks, (now + TimeSpan.FromSeconds(1)).UtcTicks);
        foreach (KeyValuePair<Message, Reservation> entry in _reservations)
        {
            if (entry.Value.ExpiresAt <= now && _reservations.TryRemove(entry))
            {
                entry.Value.Release();
            }
        }
    }

    /// <summary>The router's deferred validator: runs every check that can run now and waits until the verdict is given or abandoned.</summary>
    /// <remarks>The router drops the pending message once the returned task ends, so it ends only after the verdict reached the router.</remarks>
    public async Task ValidateAsync(PeerId source, Message message)
    {
        // A message whose reservation expired before dispatch was released already; it is validated unreserved.
        Action? release = _reservations.TryRemove(message, out Reservation? reservation) ? reservation.Release : null;
        GossipVerdict verdict = new(validity => router.CompleteValidation(message, validity), release);
        try
        {
            MessageValidity validity;
            try
            {
                validity = validator.Validate(message, verdict);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A local fault is not the sender's: no penalty, and the router caches no id for a later copy.
                if (logger.IsError) logger.Error($"Gossip validation on {message.Topic} from {source} failed", e);
                validity = MessageValidity.Throttled;
            }

            if (!verdict.IsHandedOff)
            {
                verdict.Complete(validity);
            }

            await verdict.Completion.WaitAsync(timeout, stopping);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            verdict.Abandon();
        }
    }

    /// <summary>The room <see cref="Verify"/> reserved for one deferred message, released exactly once: by its verdict, or by expiry if never dispatched.</summary>
    private sealed class Reservation(DeferredGossipValidation owner, PeerId source, long size, bool vote, bool sharedBySource, DateTimeOffset expiresAt)
    {
        private int _released;

        public DateTimeOffset ExpiresAt => expiresAt;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            if (vote)
            {
                Interlocked.Decrement(ref owner._pendingVotes);
                return;
            }

            Interlocked.Decrement(ref owner._pending);
            Interlocked.Add(ref owner._pendingBytes, -size);
            if (sharedBySource && owner._pendingBySource.AddOrUpdate(source, 0, static (_, count) => count - 1) <= 0)
            {
                owner._pendingBySource.TryRemove(new KeyValuePair<PeerId, int>(source, 0));
            }
        }
    }

    private static bool IsVote(Message message, long size) => size <= MaxVoteMessageBytes && GossipMessageValidator.IsVote(message.Topic);

    private MessageValidity Throttle(PeerId source, Message message)
    {
        Interlocked.Increment(ref Metrics.GossipThrottledCount);
        if (logger.IsDebug) logger.Debug($"Throttling gossip on {message.Topic} from {source}: {Pending} messages, {PendingBytes} bytes and {PendingVotes} votes await a verdict");
        return MessageValidity.Throttled;
    }
}
