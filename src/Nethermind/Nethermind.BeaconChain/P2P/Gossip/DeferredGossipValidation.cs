// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Caching;
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
/// A peer with no accepted vote since it joined or since its last rejected vote (or no longer among the last
/// <see cref="AcceptedVoteSourcesCapacity"/> peers with one; the set is not kept across restarts) gets a smaller fraction of that bound, and all
/// such peers together a quarter of it: a new peer id costs nothing, so a fraction per peer alone grows with the ids one host makes, and one
/// relayed valid vote must not keep an id's forgeries admitted. A peer whose vote is rejected has its new votes refused for the rest of the
/// wall slot (at most <paramref name="maxPendingVotes"/> peers per slot), or until one of its votes already being checked is accepted. Its votes
/// already deferred are still checked here, and nothing unchecked counts against it; <see cref="GossipRouter"/>'s own per-slot rule for payload
/// attestations still throttles a peer's queued payload attestations once fork choice rejected one of them.
/// Columns have a separate count and byte budget of the same size as the main budget; a column parked for its unseen block leaves it,
/// held to the parking bounds of <see cref="ColumnGossipRouter"/> instead.
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

    /// <summary>The most peers with an accepted vote that keep their standing; past it the least recently promoted one is new again.</summary>
    internal const int AcceptedVoteSourcesCapacity = 1024;

    // One delivering peer's share of a bound, as the router's own per-peer bounds.
    private const int SourceShareDivisor = 8;
    // A new peer's share of the vote bound: 2 of 1,024, so a peer id, which costs nothing, buys two checks at a time.
    private const int NewVoteSourceShareDivisor = 512;
    // All new peers' share of the vote bound, so new ids together cannot take the room of the peers whose votes were accepted.
    private const int AllNewVoteSourcesShareDivisor = 4;

    // Past the router's own expiry of a message it has not dispatched: it skips that message, so the reservation is released here instead.
    private static readonly TimeSpan ReservationSlack = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Message, Reservation> _reservations = new(ReferenceEqualityComparer.Instance);
    private long _nextSweepTicks;
    private readonly int _maxPendingPerSource = Math.Max(1, maxPending / SourceShareDivisor);
    private readonly int _maxPendingVotesPerSource = Math.Max(1, maxPendingVotes / SourceShareDivisor);
    private readonly int _maxPendingVotesPerNewSource = Math.Max(1, maxPendingVotes / NewVoteSourceShareDivisor);
    private readonly int _maxPendingVotesOfAllNewSources = Math.Max(1, maxPendingVotes / AllNewVoteSourcesShareDivisor);
    private readonly ConcurrentDictionary<(PeerId Source, bool Vote, bool Column), int> _pendingBySource = new();
    // Only a verified vote adds a peer, so peer id churn alone cannot evict the peers that sent one; its next rejected vote removes it.
    private readonly LruKeyCache<PeerId> _acceptedVoteSources = new(AcceptedVoteSourcesCapacity, "beacon gossip vote-accepted peers");
    private int _pending;
    private long _pendingBytes;
    private int _pendingVotes;
    private int _pendingColumns;
    private long _pendingColumnBytes;
    private int _pendingVotesOfNewSources;
    // Guards the rejected set and its slot. A peer's promotion, demotion and mark change together under it, and admission reads the mark under
    // it; admission reads the accepted set outside it, which at worst admits one vote of a peer being promoted as a new peer's vote.
    private readonly Lock _rejectedVoteSourcesLock = new();
    private readonly HashSet<PeerId> _rejectedVoteSources = [];
    private ulong _rejectedVoteSourcesSlot;

    internal Action<PeerId, string>? ReportRejectedMessage { get; init; }

    /// <summary>The messages other than votes deferred for validation whose verdict is not given yet.</summary>
    internal int Pending => Volatile.Read(ref _pending);
    /// <summary>The bytes of <see cref="Pending"/>.</summary>
    internal long PendingBytes => Interlocked.Read(ref _pendingBytes);
    /// <summary>The votes deferred for validation whose verdict is not given yet.</summary>
    internal int PendingVotes => Volatile.Read(ref _pendingVotes);
    /// <summary>The columns deferred for validation whose verdict is not given yet and that are not parked.</summary>
    internal int PendingColumns => Volatile.Read(ref _pendingColumns);
    /// <summary>The most messages other than votes and columns from one delivering peer that may await a verdict at once.</summary>
    internal int MaxPendingPerSource => _maxPendingPerSource;
    /// <summary>The most votes from one delivering peer with an accepted vote that may await a verdict at once.</summary>
    internal int MaxPendingVotesPerSource => _maxPendingVotesPerSource;
    /// <summary>The most votes from one delivering peer with no accepted vote since it joined or since its last rejected one that may await a verdict at once.</summary>
    internal int MaxPendingVotesPerNewSource => _maxPendingVotesPerNewSource;
    /// <summary>The most votes from all delivering peers with no accepted vote since they joined or since their last rejected one that may await a verdict at once.</summary>
    internal int MaxPendingVotesOfAllNewSources => _maxPendingVotesOfAllNewSources;

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
        bool column = GossipMessageValidator.IsColumn(message.Topic);
        bool newVoteSource = vote && !_acceptedVoteSources.Get(source);
        if (newVoteSource && IsVoteSourceRejectedThisSlot(source))
        {
            return RefuseVote(source, message);
        }

        int sourceLimit = !vote ? _maxPendingPerSource : newVoteSource ? _maxPendingVotesPerNewSource : _maxPendingVotesPerSource;
        bool overSourceShare = _pendingBySource.GetValueOrDefault((source, vote, column)) >= sourceLimit;
        bool overBound = vote ? PendingVotes >= maxPendingVotes || (newVoteSource && Volatile.Read(ref _pendingVotesOfNewSources) >= _maxPendingVotesOfAllNewSources)
            : column ? Volatile.Read(ref _pendingColumns) >= maxPending || Interlocked.Read(ref _pendingColumnBytes) + size > maxPendingBytes
            : Pending >= maxPending || PendingBytes + size > maxPendingBytes;
        if (overSourceShare || overBound)
        {
            return Throttle(source, message);
        }

        Reservation reservation = new(this, source, vote ? 0 : size, vote, column, newVoteSource, _time.GetUtcNow() + timeout + ReservationSlack);
        _pendingBySource.AddOrUpdate((source, vote, column), 1, static (_, count) => count + 1);
        if (vote)
        {
            Interlocked.Increment(ref _pendingVotes);
            if (newVoteSource)
            {
                Interlocked.Increment(ref _pendingVotesOfNewSources);
            }
        }
        else if (column)
        {
            Interlocked.Increment(ref _pendingColumns);
            Interlocked.Add(ref _pendingColumnBytes, size);
        }
        else
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Add(ref _pendingBytes, size);
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
        bool vote = reservation?.Vote ?? IsVote(message, message.CalculateSize());
        GossipVerdict verdict = new(validity =>
        {
            // Before the router applies the verdict, so an admission the router orders after it already sees the peer's new standing.
            if (vote && validity == MessageValidity.Accepted)
            {
                MarkVoteSourceAccepted(source);
            }
            else if (vote && validity == MessageValidity.Rejected)
            {
                MarkVoteSourceRejected(source);
            }

            bool applied = router.CompleteValidation(message, validity);
            if (validity == MessageValidity.Rejected)
            {
                ReportRejectedMessage?.Invoke(source, $"rejected gossip on {message.Topic}");
            }

            return applied;
        }, release);
        try
        {
            MessageValidity validity;
            try
            {
                validity = validator.Validate(message, verdict, source);
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

    /// <summary>Promotes <paramref name="source"/>, whose vote was accepted, and lifts its refusal for the slot.</summary>
    /// <remarks>Under the lock, so a refusal check ordered after this sees no mark, and a rejection ordered after this demotes and marks the peer again.</remarks>
    private void MarkVoteSourceAccepted(PeerId source)
    {
        lock (_rejectedVoteSourcesLock)
        {
            _acceptedVoteSources.Set(source);
            _rejectedVoteSources.Remove(source);
        }
    }

    /// <summary>Demotes <paramref name="source"/>, whose vote was rejected, and refuses its new votes for the rest of the wall slot.</summary>
    /// <remarks>
    /// The peer loses its standing even if one of its votes was accepted before: it is held to a new peer's share until its next vote is accepted,
    /// so one valid vote does not keep a peer's later votes admitted. At most <c>maxPendingVotes</c> peers are marked per slot, which bounds
    /// the set against peer id churn; a peer past that cap is demoted but not refused.
    /// </remarks>
    private void MarkVoteSourceRejected(PeerId source)
    {
        lock (_rejectedVoteSourcesLock)
        {
            _acceptedVoteSources.Delete(source);
            HashSet<PeerId> sources = RejectedVoteSourcesOfThisSlot();
            if (sources.Count < maxPendingVotes)
            {
                sources.Add(source);
            }
        }
    }

    /// <summary>Whether a vote of <paramref name="source"/> was rejected in the current wall slot and none accepted since.</summary>
    private bool IsVoteSourceRejectedThisSlot(PeerId source)
    {
        lock (_rejectedVoteSourcesLock)
        {
            return RejectedVoteSourcesOfThisSlot().Contains(source);
        }
    }

    /// <summary>The peers marked in the current wall slot; the set is cleared on the first use in a new slot.</summary>
    /// <remarks>The caller holds <see cref="_rejectedVoteSourcesLock"/>.</remarks>
    private HashSet<PeerId> RejectedVoteSourcesOfThisSlot()
    {
        ulong slot = validator.CurrentSlot;
        if (slot != _rejectedVoteSourcesSlot)
        {
            _rejectedVoteSourcesSlot = slot;
            _rejectedVoteSources.Clear();
        }

        return _rejectedVoteSources;
    }

    /// <summary>The room <see cref="Verify"/> reserved for one deferred message, released exactly once: by its verdict, or by expiry if never dispatched.</summary>
    private sealed class Reservation(DeferredGossipValidation owner, PeerId source, long size, bool vote, bool column, bool newVoteSource, DateTimeOffset expiresAt)
    {
        private int _released;

        public DateTimeOffset ExpiresAt => expiresAt;

        public bool Vote => vote;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            if (owner._pendingBySource.AddOrUpdate((source, vote, column), 0, static (_, count) => count - 1) <= 0)
            {
                owner._pendingBySource.TryRemove(new KeyValuePair<(PeerId, bool, bool), int>((source, vote, column), 0));
            }

            if (vote)
            {
                Interlocked.Decrement(ref owner._pendingVotes);
                if (newVoteSource)
                {
                    Interlocked.Decrement(ref owner._pendingVotesOfNewSources);
                }

                return;
            }

            if (column)
            {
                Interlocked.Decrement(ref owner._pendingColumns);
                Interlocked.Add(ref owner._pendingColumnBytes, -size);
                return;
            }

            Interlocked.Decrement(ref owner._pending);
            Interlocked.Add(ref owner._pendingBytes, -size);
        }
    }

    private static bool IsVote(Message message, long size) => size <= MaxVoteMessageBytes && GossipMessageValidator.IsVote(message.Topic);

    private MessageValidity Throttle(PeerId source, Message message)
    {
        Interlocked.Increment(ref Metrics.GossipThrottledCount);
        if (logger.IsDebug) logger.Debug($"Throttling gossip on {message.Topic} from {source}: {Pending} messages, {PendingBytes} bytes and {PendingVotes} votes await a verdict");
        return MessageValidity.Throttled;
    }

    private MessageValidity RefuseVote(PeerId source, Message message)
    {
        Interlocked.Increment(ref Metrics.GossipVotesRefusedAfterRejectionCount);
        if (logger.IsDebug) logger.Debug($"Refusing a vote on {message.Topic} from {source}: a vote it delivered was rejected in this slot");
        return MessageValidity.Throttled;
    }
}
