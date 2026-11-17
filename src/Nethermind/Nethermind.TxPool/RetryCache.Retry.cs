// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool;

public sealed partial class RetryCache<TMessage, TResourceId>
{
    private const int AccountBucketCount = 16;
    private readonly AccountBucket[] _accountBuckets = CreateAccountBuckets();
    private int _trackedHandlers;

    private sealed class AccountBucket
    {
        internal readonly Lock Sync = new();
        internal readonly Dictionary<object, SourceAccount> Accounts = [with(IdentityComparer.Instance)];
    }

    private static AccountBucket[] CreateAccountBuckets()
    {
        AccountBucket[] buckets = new AccountBucket[AccountBucketCount];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new();
        return buckets;
    }

    private AccountBucket GetBucket(object identity) =>
        _accountBuckets[IdentityComparer.Instance.GetHashCode(identity) & (AccountBucketCount - 1)];

    private readonly Lock _indexLock = new();
    private const int MaxPooledIndexNodes = 2048;
    private readonly Stack<RetryState> _unusedStates = new();
    private readonly Stack<Association> _unusedAssociations = new();
    private readonly Dictionary<object, PeerState> _peers = [with(IdentityComparer.Instance)];
    private readonly IndexedHeap<PeerState> _futurePeers = new();
    private readonly SortedSet<PeerState> _readyPeers = [with(PeerSequenceComparer.Instance)];
    private readonly IndexedHeap<RetryState> _localExpiry = new();
    private long _indexSequence;
    private long _readyCursor;
    private static readonly List<RetryState> EmptySelections = [];
    private RetrySelectionScratch? _selectionScratch;
    [ThreadStatic] private static DispatchContext _dispatchContext;

    internal int ReadyAssociationsInUse => SumPeers(static peer => peer.Associations.Count);
    internal int RetainedAssociationsInUse => SumPeers(static peer => peer.References);

    private int SumPeers(Func<PeerState, int> selector)
    {
        lock (_indexLock)
        {
            int count = 0;
            foreach (PeerState peer in _peers.Values) count += selector(peer);
            return count;
        }
    }

    internal int RetainedSelectionCapacity => Volatile.Read(ref _selectionScratch)?.Capacity ?? 0;

    private sealed class RetrySelectionScratch
    {
        internal readonly List<RetryState> Selected = [with(MaxRetryResourcesPerTick)];
        internal readonly List<PeerState> Ready = [];
        internal readonly List<int> Counts = [];
        internal readonly List<Association> A = [with(MaxRetryResourcesPerTick)];
        internal readonly List<Association> B = [with(MaxRetryResourcesPerTick)];
        internal readonly List<Association> Removed = [];
        internal readonly HashSet<RetryState> Seen = [];
        internal readonly Dictionary<RetryState, Association> AKeys = [];
        internal readonly Dictionary<RetryState, Association> BKeys = [];
        internal readonly PeerState Low = new(new object());
        internal readonly PeerState High = new(new object());

        internal int Capacity => Selected.Capacity + Ready.Capacity + Counts.Capacity + A.Capacity + B.Capacity
            + Removed.Capacity + Seen.EnsureCapacity(0) + 2 * (AKeys.EnsureCapacity(0) + BKeys.EnsureCapacity(0));

        internal bool Reset()
        {
            Selected.Clear();
            Ready.Clear();
            Counts.Clear();
            A.Clear();
            B.Clear();
            Removed.Clear();
            Seen.Clear();
            AKeys.Clear();
            BKeys.Clear();
            return Capacity <= 16_384;
        }
    }

    internal (int Tracked, int Overflow) HandlerReferences(IMessageHandler<TMessage> handler)
    {
        object identity = Identity(handler);
        AccountBucket bucket = GetBucket(identity);
        lock (bucket.Sync)
            return bucket.Accounts.TryGetValue(identity, out SourceAccount? account)
                ? (account.Tracked, account.Overflow) : (0, 0);
    }

    private long TimeoutTicks => (long)(_timeout.TotalSeconds * _timeProvider.TimestampFrequency);
    private long LocalLifetimeTicks => (long)((long)_checkMs * MaxLocalRefusals / 1000d * _timeProvider.TimestampFrequency);
    private static object Identity(IMessageHandler<TMessage> handler) => handler is ITxPoolPeer peer ? peer.Id : handler;

    private sealed class IdentityComparer : IEqualityComparer<object>
    {
        internal static readonly IdentityComparer Instance = new();
        public new bool Equals(object? x, object? y) => x is PublicKey key && y is PublicKey other
            ? key.Equals(other) : ReferenceEquals(x, y);
        public int GetHashCode(object value) => value is PublicKey key ? key.GetHashCode() : RuntimeHelpers.GetHashCode(value);
    }

    private sealed class SourceAccount(object identity, AccountBucket bucket)
    {
        internal readonly object Identity = identity;
        internal readonly AccountBucket Bucket = bucket;
        internal int Tracked;
        internal int Overflow;
        internal long ProgressEpoch;
        internal long PaymentSequence;
        internal long ProgressRunStartedAt;
        internal long LastProgressAt;
    }

    private static SourceAccount GetAccount(AccountBucket bucket, object identity)
    {
        if (!bucket.Accounts.TryGetValue(identity, out SourceAccount? account))
            bucket.Accounts.Add(identity, account = new(identity, bucket));
        return account;
    }

    private bool ReserveHandlerSlot(IMessageHandler<TMessage> handler, bool overflow)
    {
        object identity = Identity(handler);
        AccountBucket bucket = GetBucket(identity);
        lock (bucket.Sync)
        {
            SourceAccount account = GetAccount(bucket, identity);
            ref int count = ref overflow ? ref account.Overflow : ref account.Tracked;
            if (count < (overflow ? MaxDeferredOverflowResourcesPerHandler : _maxPendingResourcesPerHandler))
            {
                count++;
                if (!overflow && count == 1) Interlocked.Increment(ref _trackedHandlers);
                return true;
            }
            if (account.Tracked == 0 && account.Overflow == 0) bucket.Accounts.Remove(account.Identity);
            return false;
        }
    }

    private void ReleaseHandlerSlot(IMessageHandler<TMessage> handler, bool overflow)
    {
        object identity = Identity(handler);
        AccountBucket bucket = GetBucket(identity);
        lock (bucket.Sync)
        {
            if (!bucket.Accounts.TryGetValue(identity, out SourceAccount? account)) return;
            ref int count = ref overflow ? ref account.Overflow : ref account.Tracked;
            if (count > 0)
            {
                count--;
                if (!overflow && count == 0) Interlocked.Decrement(ref _trackedHandlers);
            }
            if (account.Tracked == 0 && account.Overflow == 0)
            {
                bucket.Accounts.Remove(account.Identity);
                if (bucket.Accounts.Count == 0 && bucket.Accounts.EnsureCapacity(0) > 2048 / AccountBucketCount) bucket.Accounts.TrimExcess();
            }
        }
    }

    internal void CaptureRequestTokens(ReadOnlySpan<TResourceId> resources, IMessageHandler<TMessage> sender,
        Span<RetryRequestToken<TResourceId>> tokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tokens.Length, resources.Length);
        tokens.Clear();
        if (!TryEnterOperation()) return;
        try
        {
            lock (_indexLock)
            {
                long now = _timeProvider.GetTimestamp();
                RetryRequestConfirmation? confirmation = null;
                for (int i = 0; i < resources.Length; i++)
                {
                    TResourceId resource = resources[i];
                    while (_retryRequests.TryGetValue(resource, out RetryRequestEntry entry))
                    {
                        long dispatchGeneration = GetDispatchGeneration(resource, sender);
                        if (dispatchGeneration != 0 && dispatchGeneration != entry.RequestGeneration) break;
                        if (entry.SourceHandler is null || entry.State is { } state && !IsLive(state, now)
                            || !ReferenceEquals(entry.SourceHandler, sender)
                            && !ReferenceEquals(entry.State?.SelectedHandler, sender)) break;
                        RetryRequestEntry sent = entry with
                        {
                            RequestedHandler = sender,
                            RequestStartedAt = now,
                            Attempt = entry.Attempt + 1
                        };
                        if (!_retryRequests.TryUpdate(resource, sent, entry)) continue;
                        confirmation ??= new();
                        tokens[i] = new(resource, sent.RequestGeneration, sent.Attempt, now + TimeoutTicks, confirmation);
                        if (sent.State is { } current)
                        {
                            current.RequestDue = now + TimeoutTicks;
                            RefreshAssociations(current);
                        }
                        break;
                    }
                }
            }
        }
        finally { ExitOperation(); }
    }

    internal object? CaptureUsefulProgress(IMessageHandler<TMessage> sender, in RetryRequestToken<TResourceId> token)
    {
        lock (_indexLock)
        {
            long now = _timeProvider.GetTimestamp();
            if (token.Generation == 0 || token.Confirmation is null
                || Volatile.Read(ref token.Confirmation.State) == 2 || now >= token.Deadline
                || !_retryRequests.TryGetValue(token.ResourceId, out RetryRequestEntry entry)
                || entry.RequestGeneration != token.Generation || entry.Attempt != token.Attempt
                || !ReferenceEquals(entry.RequestedHandler, sender)
                || entry.State is { } state && !IsLive(state, now)) return null;
            object identity = Identity(sender);
            AccountBucket bucket = GetBucket(identity);
            lock (bucket.Sync) return bucket.Accounts.GetValueOrDefault(identity);
        }
    }

    internal void RecordUsefulProgress(object? captured, in RetryRequestToken<TResourceId> token)
    {
        if (captured is not SourceAccount account || token.Confirmation is not { } confirmation) return;
        lock (confirmation.Sync)
        {
            if (confirmation.State == 2) return;
            lock (account.Bucket.Sync)
            {
                long now = _timeProvider.GetTimestamp();
                if (now >= token.Deadline || !IsCurrentAccount(account)) return;
                long sequence = ++account.PaymentSequence;
                if (confirmation.State == 1) PayProgress(account, sequence, now);
                else
                {
                    confirmation.PendingAccount = account;
                    confirmation.PendingSequence = sequence;
                    confirmation.PendingDeadline = token.Deadline;
                }
            }
        }
    }

    internal void CompleteRequest(RetryRequestConfirmation? confirmation, bool sent)
    {
        if (confirmation is null) return;
        lock (confirmation.Sync)
        {
            if (confirmation.State != 0) return;
            Volatile.Write(ref confirmation.State, sent ? 1 : 2);
            if (sent && confirmation.PendingAccount is SourceAccount account)
            {
                lock (account.Bucket.Sync)
                {
                    long now = _timeProvider.GetTimestamp();
                    if (now < confirmation.PendingDeadline && IsCurrentAccount(account))
                        PayProgress(account, confirmation.PendingSequence, now);
                }
            }
            confirmation.PendingAccount = null;
        }
    }

    private bool IsCurrentAccount(SourceAccount account) =>
        account.Bucket.Accounts.TryGetValue(account.Identity, out SourceAccount? current) && ReferenceEquals(current, account);

    private void PayProgress(SourceAccount account, long sequence, long now)
    {
        if (sequence <= account.ProgressEpoch) return;
        if (account.ProgressEpoch == 0 || now - account.LastProgressAt >= LocalLifetimeTicks)
            account.ProgressRunStartedAt = now;
        account.LastProgressAt = now;
        account.ProgressEpoch = sequence;
    }

    private bool IsLive(RetryState state, long now)
    {
        if (state.Terminal || state.LogicallyExpired) return false;
        if (!state.HasLocalLifetime) return true;
        SourceAccount source = state.Source;
        lock (source.Bucket.Sync)
        {
            if (source.ProgressEpoch > state.ProgressEpoch && source.ProgressRunStartedAt < state.Due)
            {
                state.ProgressEpoch = source.ProgressEpoch;
                state.LocalRefusals = 0;
                state.Due = source.LastProgressAt + LocalLifetimeTicks;
                _localExpiry.Fix(state);
            }
        }
        bool live = now < state.Due && state.LocalRefusals <= MaxLocalRefusals;
        if (!live) state.LogicallyExpired = true;
        return live;
    }

    private abstract class HeapNode
    {
        internal int HeapIndex = -1;
        internal long Due;
        internal long Sequence;
    }

    private sealed class RetryState : HeapNode
    {
        internal TResourceId Resource = default!;
        internal long Generation;
        internal SourceAccount Source = null!;
        internal long RequestDue;
        internal Association? Associations;
        internal bool HasLocalLifetime;
        internal int LocalRefusals;
        internal long ProgressEpoch;
        internal bool Claimed;
        internal bool Terminal;
        internal bool LogicallyExpired;
        internal bool TicketDetached;
        internal IMessageHandler<TMessage>? SelectedHandler;
        internal bool SelectedOwnsSlot;
    }

    private sealed class Association : HeapNode
    {
        internal RetryState State = null!;
        internal PeerState Peer = null!;
        internal IMessageHandler<TMessage> Handler = null!;
        internal bool Local;
        internal Association? Next;
    }

    private sealed class PeerState(object identity) : HeapNode
    {
        internal readonly object Identity = identity;
        internal readonly IndexedHeap<Association> Associations = new();
        internal int References;
        internal bool Ready;
    }

    private sealed class PeerSequenceComparer : IComparer<PeerState>
    {
        internal static readonly PeerSequenceComparer Instance = new();
        public int Compare(PeerState? x, PeerState? y) => x!.Sequence.CompareTo(y!.Sequence);
    }

    private sealed class IndexedHeap<T> where T : HeapNode
    {
        private readonly List<T> _items = [];
        internal int Count => _items.Count;
        internal T? Head => _items.Count == 0 ? null : _items[0];

        internal void Add(T item)
        {
            if (item.HeapIndex >= 0) { Fix(item); return; }
            item.HeapIndex = _items.Count;
            _items.Add(item);
            Up(item.HeapIndex);
        }

        internal void Remove(T item)
        {
            int index = item.HeapIndex;
            if (index < 0) return;
            int last = _items.Count - 1;
            if (index != last)
            {
                _items[index] = _items[last];
                _items[index].HeapIndex = index;
            }
            _items.RemoveAt(last);
            item.HeapIndex = -1;
            if (index < _items.Count) { Down(index); Up(index); }
            if (_items.Count == 0 && _items.Capacity > 16_384) _items.TrimExcess();
        }

        internal void Fix(T item)
        {
            if (item.HeapIndex < 0) return;
            Up(item.HeapIndex);
            Down(item.HeapIndex);
        }

        private static bool Earlier(T first, T second) => first.Due < second.Due
            || first.Due == second.Due && first.Sequence < second.Sequence;

        private void Up(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (!Earlier(_items[index], _items[parent])) break;
                Swap(index, parent);
                index = parent;
            }
        }

        private void Down(int index)
        {
            while (index * 2 + 1 < _items.Count)
            {
                int child = index * 2 + 1;
                if (child + 1 < _items.Count && Earlier(_items[child + 1], _items[child])) child++;
                if (!Earlier(_items[child], _items[index])) break;
                Swap(index, child);
                index = child;
            }
        }

        private void Swap(int first, int second)
        {
            (_items[first], _items[second]) = (_items[second], _items[first]);
            _items[first].HeapIndex = first;
            _items[second].HeapIndex = second;
        }
    }

    private bool TryGetCurrent(RetryState state, out RetryRequestEntry entry) =>
        _retryRequests.TryGetValue(state.Resource, out entry) && ReferenceEquals(entry.State, state);

    private RetryState? GetState(in TResourceId resource, ref RetryRequestEntry entry)
    {
        long generation = entry.RequestGeneration;
        while (entry.State is null)
        {
            if (entry.SourceHandler is null) return null;
            SourceAccount source;
            object sourceIdentity = Identity(entry.SourceHandler);
            AccountBucket bucket = GetBucket(sourceIdentity);
            lock (bucket.Sync) source = GetAccount(bucket, sourceIdentity);
            RetryState state = _unusedStates.TryPop(out RetryState? unused) ? unused : new();
            state.Resource = resource;
            state.Generation = entry.RequestGeneration;
            state.Source = source;
            state.RequestDue = entry.RequestStartedAt + TimeoutTicks;
            state.Sequence = ++_indexSequence;
            state.Terminal = false;
            RetryRequestEntry indexed = entry with { State = state };
            if (_retryRequests.TryUpdate(resource, indexed, entry)) { entry = indexed; return state; }
            if (!_retryRequests.TryGetValue(resource, out entry) || entry.RequestGeneration != generation) return null;
        }
        return entry.State;
    }

    private void PublishAssociation(in TResourceId resource, IMessageHandler<TMessage> handler, long generation, bool local = false)
    {
        lock (_indexLock)
        {
            if (!_retryRequests.TryGetValue(resource, out RetryRequestEntry entry) || entry.RequestGeneration != generation) return;
            RetryState? state = GetState(resource, ref entry);
            if (state is null || state.Terminal) return;
            for (Association? old = state.Associations; old is not null; old = old.Next)
                if (ReferenceEquals(old.Handler, handler) && old.Local == local)
                {
                    if (local) old.Sequence = ++_indexSequence;
                    RefreshAssociations(state);
                    return;
                }
            object identity = Identity(handler);
            if (!_peers.TryGetValue(identity, out PeerState? peer))
                _peers.Add(identity, peer = new(identity) { Sequence = ++_indexSequence });
            Association association = _unusedAssociations.TryPop(out Association? unused) ? unused : new();
            association.State = state;
            association.Peer = peer;
            association.Handler = handler;
            association.Local = local;
            association.Sequence = ++_indexSequence;
            association.Next = state.Associations;
            association.Due = state.RequestDue;
            state.Associations = association;
            peer.References++;
            RefreshAssociations(state);
        }
    }

    private void RefreshPeer(PeerState peer)
    {
        if (peer.Associations.Head is { } head)
        {
            peer.Due = head.Due;
            bool ready = peer.Due <= _timeProvider.GetTimestamp();
            if (ready) _futurePeers.Remove(peer);
            SetReady(peer, ready);
            if (!ready) _futurePeers.Add(peer);
        }
        else
        {
            _futurePeers.Remove(peer);
            SetReady(peer, false);
            if (peer.References == 0)
            {
                _peers.Remove(peer.Identity);
                if (_peers.Count == 0 && _peers.EnsureCapacity(0) > 16_384) _peers.TrimExcess();
            }
        }
    }

    private void SetReady(PeerState peer, bool ready)
    {
        if (peer.Ready == ready) return;
        if (ready) _readyPeers.Add(peer);
        else _readyPeers.Remove(peer);
        peer.Ready = ready;
    }

    private void RefreshAssociations(RetryState state)
    {
        bool pendingAlternate = false;
        for (Association? item = state.Associations; item is not null; item = item.Next)
            if (!item.Local) { pendingAlternate = true; break; }
        for (Association? item = state.Associations; item is not null; item = item.Next)
        {
            if (state.Claimed || state.Terminal || state.LogicallyExpired || item.Local && pendingAlternate) item.Peer.Associations.Remove(item);
            else { item.Due = state.RequestDue; item.Peer.Associations.Add(item); }
            RefreshPeer(item.Peer);
        }
    }

    private void RemoveAssociation(RetryState state, Association association)
    {
        Association? previous = null;
        for (Association? item = state.Associations; item is not null; item = item.Next)
        {
            if (ReferenceEquals(item, association))
            {
                if (previous is null) state.Associations = item.Next;
                else previous.Next = item.Next;
                item.Peer.Associations.Remove(item);
                item.Peer.References--;
                RefreshPeer(item.Peer);
                return;
            }
            previous = item;
        }
    }

    private void RetireState(RetryState? state, bool entryRemoved = false)
    {
        if (state is null) return;
        lock (_indexLock)
        {
            if (state.Terminal) return;
            state.Terminal = true;
            _localExpiry.Remove(state);
            // A claimed state can still be referenced by a dispatch outside the index lock.
            // Only the owner of a removed dictionary entry can return its nodes.
            bool recycle = entryRemoved && !state.Claimed && !state.HasLocalLifetime;
            while (state.Associations is { } item)
            {
                RemoveAssociation(state, item);
                if (recycle && _unusedAssociations.Count < MaxPooledIndexNodes)
                {
                    item.State = null!;
                    item.Peer = null!;
                    item.Handler = null!;
                    item.Next = null;
                    _unusedAssociations.Push(item);
                }
            }
            if (state.TicketDetached)
            {
                state.TicketDetached = false;
                Interlocked.Decrement(ref _expiringQueueCounter);
            }
            ReleaseSelectedSlot(state);
            if (recycle && _unusedStates.Count < MaxPooledIndexNodes)
            {
                state.Resource = default!;
                state.Source = null!;
                state.SelectedHandler = null;
                _unusedStates.Push(state);
            }
        }
    }

    private void ReleaseSelectedSlot(RetryState state)
    {
        if (!state.SelectedOwnsSlot || state.SelectedHandler is not { } selected) return;
        state.SelectedOwnsSlot = false;
        ReleaseHandlerSlot(selected);
    }

    private void ExpireLocalRequests(ref int work)
    {
        long now = _timeProvider.GetTimestamp();
        while (work < 8192 && _localExpiry.Head is { } state && state.Due <= now)
        {
            work++;
            if (IsLive(state, now)) { _localExpiry.Fix(state); continue; }
            _localExpiry.Remove(state);
            if (TryGetCurrent(state, out RetryRequestEntry entry)) TryRemove(state.Resource, entry);
            else RetireState(state);
        }
    }

    private List<RetryState> SelectRetryRequests(ref int work, out RetrySelectionScratch? scratch)
    {
        long now = _timeProvider.GetTimestamp();
        while (work < 8192 && _futurePeers.Head is { } future && future.Due <= now)
        {
            work++;
            _futurePeers.Remove(future);
            SetReady(future, true);
        }
        scratch = null;
        if (_readyPeers.Count == 0) return EmptySelections;
        scratch = Interlocked.Exchange(ref _selectionScratch, null) ?? new();
        List<RetryState> selected = scratch.Selected;
        if (_readyPeers.Count == 2)
        {
            using SortedSet<PeerState>.Enumerator peers = _readyPeers.GetEnumerator();
            peers.MoveNext();
            PeerState first = peers.Current;
            peers.MoveNext();
            PeerState second = peers.Current;
            if (first.Sequence <= _readyCursor && second.Sequence > _readyCursor) (first, second) = (second, first);
            SelectTwoPeers(first, second, scratch, ref work, now);
            return selected;
        }

        List<PeerState> ready = scratch.Ready;
        PeerState low = scratch.Low;
        PeerState high = scratch.High;
        for (int pass = 0; pass < 2 && work < 16_384 && ready.Count < MaxRetryResourcesPerTick; pass++)
        {
            low.Sequence = pass == 0 ? _readyCursor + 1 : 0;
            high.Sequence = pass == 0 ? long.MaxValue : _readyCursor;
            foreach (PeerState peer in _readyPeers.GetViewBetween(low, high))
            {
                if (work >= 16_384 || ready.Count == MaxRetryResourcesPerTick) break;
                work++;
                ready.Add(peer);
            }
        }
        List<int> counts = scratch.Counts;
        for (int i = 0; i < ready.Count; i++) counts.Add(0);
        bool found;
        do
        {
            found = false;
            for (int i = 0; i < ready.Count && work < 16_384 && selected.Count < MaxRetryResourcesPerTick; i++)
            {
                PeerState peer = ready[i];
                if (counts[i] >= MaxRetryResourcesPerHandlerPerTick) continue;
                while (work < 16_384 && peer.Associations.Head is { } candidate && candidate.Due <= now)
                {
                    work++;
                    if (TryClaimAssociation(candidate, now))
                    {
                        counts[i]++;
                        selected.Add(candidate.State);
                        found = true;
                        _readyCursor = peer.Sequence;
                        break;
                    }
                }
            }
        } while (found && work < 16_384 && selected.Count < MaxRetryResourcesPerTick);
        return selected;
    }

    private void CollectPeerCandidates(PeerState peer, List<Association> candidates, RetrySelectionScratch scratch,
        ref int work, long now)
    {
        List<Association> removed = scratch.Removed;
        HashSet<RetryState> seen = scratch.Seen;
        removed.Clear();
        seen.Clear();
        while (work < 16_384 && candidates.Count < MaxRetryResourcesPerTick
            && peer.Associations.Head is { } candidate && candidate.Due <= now)
        {
            work++;
            peer.Associations.Remove(candidate);
            removed.Add(candidate);
            RetryState state = candidate.State;
            if (!IsLive(state, now))
            {
                SuspendExpiredAssociations(state);
                continue;
            }
            if (seen.Add(state)) candidates.Add(candidate);
        }
        foreach (Association candidate in removed)
            if (!candidate.State.Terminal && !candidate.State.LogicallyExpired) peer.Associations.Add(candidate);
        RefreshPeer(peer);
    }

    private void SelectTwoPeers(PeerState first, PeerState second, RetrySelectionScratch scratch, ref int work, long now)
    {
        List<Association> a = scratch.A;
        List<Association> b = scratch.B;
        List<RetryState> selected = scratch.Selected;
        CollectPeerCandidates(first, a, scratch, ref work, now);
        CollectPeerCandidates(second, b, scratch, ref work, now);
        Dictionary<RetryState, Association> aKeys = scratch.AKeys;
        Dictionary<RetryState, Association> bKeys = scratch.BKeys;
        foreach (Association item in a) aKeys.Add(item.State, item);
        foreach (Association item in b) bKeys.Add(item.State, item);
        int aCount = 0, bCount = 0;
        foreach (Association item in a)
            if (!bKeys.ContainsKey(item.State) && aCount < MaxRetryResourcesPerHandlerPerTick
                && TryClaimAssociation(item, now)) { selected.Add(item.State); aCount++; _readyCursor = first.Sequence; }
        foreach (Association item in b)
            if (!aKeys.ContainsKey(item.State) && bCount < MaxRetryResourcesPerHandlerPerTick
                && TryClaimAssociation(item, now)) { selected.Add(item.State); bCount++; _readyCursor = second.Sequence; }
        foreach (Association item in a)
        {
            if (!bKeys.TryGetValue(item.State, out Association? other)) continue;
            Association? choice = aCount < MaxRetryResourcesPerHandlerPerTick && aCount <= bCount ? item
                : bCount < MaxRetryResourcesPerHandlerPerTick ? other
                : aCount < MaxRetryResourcesPerHandlerPerTick ? item : null;
            if (choice is null) break;
            if (!TryClaimAssociation(choice, now)) continue;
            selected.Add(choice.State);
            _readyCursor = choice.Peer.Sequence;
            if (ReferenceEquals(choice, item)) aCount++;
            else bCount++;
        }
    }

    private bool TryClaimAssociation(Association association, long now)
    {
        RetryState state = association.State;
        if (state.Claimed || state.Terminal) return false;
        if (!IsLive(state, now))
        {
            SuspendExpiredAssociations(state);
            return false;
        }
        if (!TryGetCurrent(state, out RetryRequestEntry entry)) { RetireState(state); return false; }
        state.Claimed = true;
        RefreshAssociations(state);
        bool taken = association.Local
            ? ReferenceEquals(entry.LocallyDeferredHandler, association.Handler)
            : entry.Handlers.TryTakeSpecific(entry.Generation, association.Handler);
        if (!taken || !association.Local)
        {
            RemoveAssociation(state, association);
            RequeueUnownedTicket(state, entry);
        }
        if (!taken)
        {
            state.Claimed = false;
            RefreshAssociations(state);
            return false;
        }
        state.SelectedHandler = association.Handler;
        state.SelectedOwnsSlot = !ReferenceEquals(entry.SourceHandler, association.Handler) && !association.Local;
        return true;
    }

    private void SuspendExpiredAssociations(RetryState state)
    {
        for (Association? item = state.Associations; item is not null; item = item.Next)
        {
            item.Peer.Associations.Remove(item);
            RefreshPeer(item.Peer);
        }
    }

    private bool PrepareDispatch(RetryState state, out IMessageHandler<TMessage>? handler)
    {
        lock (_indexLock)
        {
            handler = state.SelectedHandler;
            long now = _timeProvider.GetTimestamp();
            if (!state.Claimed || state.Terminal || handler is null || !IsLive(state, now)) return false;
            while (TryGetCurrent(state, out RetryRequestEntry entry))
            {
                bool clearsLocal = ReferenceEquals(entry.LocallyDeferredHandler, handler);
                RetryRequestEntry dispatch = entry with
                {
                    LocallyDeferredHandler = clearsLocal ? null : entry.LocallyDeferredHandler,
                    RequestStartedAt = now,
                    RequestedHandler = handler
                };
                if (!_retryRequests.TryUpdate(state.Resource, dispatch, entry)) continue;
                if (clearsLocal)
                {
                    if (!ReferenceEquals(entry.SourceHandler, handler)) ReleaseHandlerSlot(handler);
                }
                ReleaseSelectedSlot(state);
                state.RequestDue = now + TimeoutTicks;
                return true;
            }
            return false;
        }
    }

    private void FinishDispatches(List<RetryState> selected)
    {
        lock (_indexLock)
        {
            foreach (RetryState state in selected)
            {
                ReleaseSelectedSlot(state);
                state.Claimed = false;
                state.SelectedHandler = null;
                if (!state.Terminal)
                {
                    if (TryGetCurrent(state, out RetryRequestEntry current))
                    {
                        for (Association? item = state.Associations; item is not null;)
                        {
                            Association? next = item.Next;
                            if (item.Local && !ReferenceEquals(current.LocallyDeferredHandler, item.Handler))
                                RemoveAssociation(state, item);
                            item = next;
                        }
                        RequeueUnownedTicket(state, current);
                    }
                    RefreshAssociations(state);
                }
            }
        }
    }

    private void RequeueUnownedTicket(RetryState state, RetryRequestEntry entry)
    {
        if (state.LogicallyExpired || !state.TicketDetached || state.Associations is not null || entry.LocallyDeferredHandler is not null) return;
        state.TicketDetached = false;
        _expiringQueue.Enqueue((state.Resource, state.Generation, entry.RequestStartedAt));
    }

    private void CleanExpiryQueue(ref int work, int limit)
    {
        while (work < limit && _expiringQueue.TryDequeueExpired(_timeProvider, _timeout,
            out (TResourceId ResourceId, long RequestGeneration, long EnqueuedAt) item))
        {
            work++;
            lock (_indexLock)
            {
                if (!_retryRequests.TryGetValue(item.ResourceId, out RetryRequestEntry entry)
                    || entry.RequestGeneration != item.RequestGeneration)
                {
                    Interlocked.Decrement(ref _expiringQueueCounter);
                    continue;
                }
                long now = _timeProvider.GetTimestamp();
                if (entry.State is { } state && (state.Claimed || state.Associations is not null))
                {
                    state.TicketDetached = true;
                    continue;
                }
                long started = entry.RequestStartedAt;
                if (now < started + TimeoutTicks)
                {
                    _expiringQueue.Enqueue((item.ResourceId, item.RequestGeneration, started));
                    continue;
                }
                if (entry.SourceHandler is not null) _requestingResources.Set(item.ResourceId);
                TryRemove(item.ResourceId, entry);
                Interlocked.Decrement(ref _expiringQueueCounter);
            }
        }
    }

    private long GetDispatchGeneration(in TResourceId resource, IMessageHandler<TMessage> handler)
    {
        ref readonly DispatchContext context = ref _dispatchContext;
        if (!ReferenceEquals(context.Cache, this) || !ReferenceEquals(context.Handler, handler)) return 0;
        if (context.State is { } single && single.Resource.Equals(resource)) return single.Generation;
        return context.States is { } states && states.TryGetValue(resource, out RetryState? state) ? state.Generation : 0;
    }

    private readonly record struct DispatchContext(RetryCache<TMessage, TResourceId>? Cache, IMessageHandler<TMessage>? Handler,
        Dictionary<TResourceId, RetryState>? States, RetryState? State);

    private readonly struct DispatchScope : IDisposable
    {
        private readonly DispatchContext _previous;

        internal DispatchScope(RetryCache<TMessage, TResourceId> cache, IMessageHandler<TMessage> handler,
            Dictionary<TResourceId, RetryState>? states, RetryState? state)
        {
            ref DispatchContext context = ref _dispatchContext;
            _previous = context;
            context = new(cache, handler, states, state);
        }

        public void Dispose() => _dispatchContext = _previous;
    }
}
