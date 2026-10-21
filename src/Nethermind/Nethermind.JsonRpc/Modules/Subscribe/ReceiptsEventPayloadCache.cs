// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain.Receipts;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Subscribe;

/// <summary>
/// Shares the payloads that the subscriptions of one factory serialize for a receipts event (its logs, its receipts),
/// so a payload several of them send is serialized once.
/// </summary>
/// <remarks>
/// <para>
/// Subscriptions join under a key of their filter: equal keys must mean filters accepting the same payloads, and a
/// <c>null</c> key one accepting all of them. Only payloads that can be sent more than once are shared: a subscription
/// stores the payloads it serializes when another one has its key, or when it is filtered and an unfiltered one exists.
/// An unfiltered subscription with no peer of its key only reads what others stored. Any other subscription, a lone one
/// or one whose filter no other shares, gets no per-event state and sends its payloads as if there were no cache.
/// </para>
/// <para>
/// Membership changes while events are delivered, but a payload depends only on its event and position, so a stale
/// view only shares less or stores a payload no one reads. Payloads are attached to the event, which every subscription
/// receives as the same instance and its queued sends keep alive, so a lagging subscription still finds them and they
/// are collected with the event. As a result, a lagging subscription also keeps alive the shared payloads of each
/// event in its queue, up to its queued-block limit.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The type of the filter keys.</typeparam>
/// <param name="payloadCount">The number of payloads an event can have, each addressed by its position.</param>
/// <param name="keyComparer">The comparer of filter keys, or <c>null</c> for the default one.</param>
internal sealed class ReceiptsEventPayloadCache<TKey>(Func<ReceiptsEventArgs, int> payloadCount, IEqualityComparer<TKey>? keyComparer = null)
    where TKey : class
{
    private readonly ConditionalWeakTable<ReceiptsEventArgs, Lazy<byte[]>?[]> _payloads = [];
    private readonly ConditionalWeakTable<ReceiptsEventArgs, Lazy<byte[]>?[]>.CreateValueCallback _createSlots = e => new Lazy<byte[]>?[payloadCount(e)];
    private readonly Dictionary<TKey, Group> _groups = new(keyComparer);
    private readonly Group _unfiltered = new(null);
    private readonly Lock _lock = new();
    private int _members;

    /// <summary>Registers a subscription until the returned membership is disposed.</summary>
    /// <param name="filterKey">The key of the subscription's filter, or <c>null</c> when it accepts every payload.</param>
    public Membership Join(TKey? filterKey)
    {
        lock (_lock)
        {
            Group group = _unfiltered;
            if (filterKey is not null && !_groups.TryGetValue(filterKey, out group!))
            {
                _groups.Add(filterKey, group = new Group(filterKey));
            }

            group.Count++;
            _members++;
            return new Membership(this, group);
        }
    }

    private void Leave(Group group)
    {
        lock (_lock)
        {
            _members--;
            if (--group.Count == 0 && group.Key is not null) _groups.Remove(group.Key);
        }
    }

    private EventPayloads ForEvent(ReceiptsEventArgs e, Group group)
    {
        bool unfiltered = ReferenceEquals(group, _unfiltered);
        if (Volatile.Read(ref group.Count) > 1 || (!unfiltered && Volatile.Read(ref _unfiltered.Count) > 0))
        {
            return new EventPayloads(_payloads.GetValue(e, _createSlots), stores: true);
        }

        return unfiltered && Volatile.Read(ref _members) > 1 && _payloads.TryGetValue(e, out Lazy<byte[]>?[]? stored)
            ? new EventPayloads(stored, stores: false)
            : default;
    }

    /// <summary>A subscription's registration with a <see cref="ReceiptsEventPayloadCache{TKey}"/>.</summary>
    internal sealed class Membership(ReceiptsEventPayloadCache<TKey> cache, Group group) : IDisposable
    {
        private int _disposed;

        /// <summary>Gets the shared payloads of <paramref name="e"/> as this subscription may use them.</summary>
        public EventPayloads ForEvent(ReceiptsEventArgs e) => cache.ForEvent(e, group);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) cache.Leave(group);
        }
    }

    internal sealed class Group(TKey? key)
    {
        public readonly TKey? Key = key;
        public int Count;
    }
}

/// <summary>The payloads of one receipts event as one subscription may share them.</summary>
/// <remarks>
/// A payload is published before it is serialized, so subscriptions reaching it at once, as ones with the same filter
/// do, wait for one serialization instead of each serializing it.
/// </remarks>
internal readonly struct EventPayloads(Lazy<byte[]>?[]? slots, bool stores)
{
    /// <summary>Whether the subscription stores the payloads it serializes for others.</summary>
    public bool Stores => stores;

    /// <summary>Gets the payload at <paramref name="position"/> if another subscription stored it.</summary>
    public bool TryGet(int position, out SerializedJson payload)
    {
        Lazy<byte[]>? json = slots is null ? null : Volatile.Read(ref slots[position]);
        payload = new SerializedJson(json?.Value!);
        return json is not null;
    }

    /// <summary>Serializes <paramref name="value"/> as the payload at <paramref name="position"/>, unless one is stored already.</summary>
    /// <remarks>Only valid when <see cref="Stores"/>.</remarks>
    public SerializedJson Store<T>(int position, T value)
    {
        Lazy<byte[]> candidate = new(() => JsonRpcResponseWriter.SerializePayload(value, EthereumJsonSerializer.JsonOptions));
        return new SerializedJson((Interlocked.CompareExchange(ref slots![position], candidate, null) ?? candidate).Value);
    }
}
