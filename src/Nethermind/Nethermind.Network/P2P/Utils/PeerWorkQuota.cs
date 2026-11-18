// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Utils;

internal sealed class PeerWorkQuota
{
    private const int ReplyLimit = 32;
    private const int ServingClassLimit = 32;
    private readonly int _ordinaryLimit;
    private readonly int _replyLimit;
    private readonly int _networkLimit;
    private readonly int _subnetLimit;
    private readonly Dictionary<NodeFilter.IpSubnetKey, int> _subnets = [];
    private int _reserved;
    private readonly Lock _lock = new();
    private readonly Dictionary<PublicKey, PeerWork> _counts = [];

    internal PeerWorkQuota(int? queueCapacity)
    {
        int budget = queueCapacity ?? 8192;
        _networkLimit = budget - budget / 16;
        _subnetLimit = Math.Max(1, budget / 4);
        if (queueCapacity is { } capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            int share = capacity / 16;
            _replyLimit = Math.Min(ReplyLimit, share / 3);
            // At capacity 8192, ordinary 448 plus two protected 32 reply classes gives 512 per identity.
            // Ordinary headroom above thirteen serving classes times 32 is 32 at 8192 and zero at 7680.
            // Transactions and NewBlock share that ordinary allowance.
            _ordinaryLimit = share - 2 * _replyLimit;
        }
        else
        {
            // Legacy schedulers expose no capacity: retain ordinary 512 plus two protected 32 reply classes.
            // This bounded compatibility fallback cannot guarantee sixteen identities for an unknown queue.
            _ordinaryLimit = 512;
            _replyLimit = ReplyLimit;
        }
    }

    internal bool TryReserve(PublicKey id, WorkKind kind, (string Protocol, int PacketType)? servingClass,
        [NotNullWhen(true)] out Reservation? reservation, NodeFilter.IpSubnetKey? source = null)
    {
        lock (_lock)
        {
            int subnetCount = source is { } subnet ? _subnets.GetValueOrDefault(subnet) : 0;
            if (_reserved >= _networkLimit || subnetCount >= _subnetLimit)
            {
                reservation = null;
                return false;
            }
            _counts.TryGetValue(id, out PeerWork? work);
            work ??= new();
            int current = kind switch
            {
                WorkKind.PooledReply => work.PooledReplies,
                WorkKind.CellsReply => work.CellsReplies,
                _ => work.Ordinary
            };
            int limit = kind == WorkKind.Ordinary ? _ordinaryLimit : _replyLimit;
            int classCount = servingClass is { } key ? work.ServingClasses.GetValueOrDefault(key) : 0;
            if (current >= limit || servingClass.HasValue && classCount >= ServingClassLimit)
            {
                reservation = null;
                return false;
            }
            if (work.Total == 0) _counts.Add(id, work);
            switch (kind)
            {
                case WorkKind.PooledReply: work.PooledReplies++; break;
                case WorkKind.CellsReply: work.CellsReplies++; break;
                default: work.Ordinary++; break;
            }
            if (servingClass is { } acceptedClass) work.ServingClasses[acceptedClass] = classCount + 1;
            _reserved++;
            if (source.HasValue) _subnets[source.Value] = subnetCount + 1;
            reservation = new(this, id, kind, servingClass, source);
            return true;
        }
    }

    private void Release(PublicKey id, WorkKind kind, (string Protocol, int PacketType)? servingClass, NodeFilter.IpSubnetKey? source)
    {
        lock (_lock)
        {
            _reserved--;
            if (source is { } subnet)
            {
                int remaining = _subnets[subnet] - 1;
                if (remaining == 0) _subnets.Remove(subnet);
                else _subnets[subnet] = remaining;
            }
            PeerWork work = _counts[id];
            switch (kind)
            {
                case WorkKind.PooledReply: work.PooledReplies--; break;
                case WorkKind.CellsReply: work.CellsReplies--; break;
                default: work.Ordinary--; break;
            }
            if (servingClass is { } key)
            {
                int remaining = work.ServingClasses[key] - 1;
                if (remaining == 0) work.ServingClasses.Remove(key);
                else work.ServingClasses[key] = remaining;
            }
            if (work.Total == 0) _counts.Remove(id);
        }
    }

    internal enum WorkKind { Ordinary, PooledReply, CellsReply }

    private sealed class PeerWork
    {
        internal int Ordinary;
        internal int PooledReplies;
        internal int CellsReplies;
        internal int Total => Ordinary + PooledReplies + CellsReplies;
        internal readonly Dictionary<(string Protocol, int PacketType), int> ServingClasses = [];
    }

    internal sealed class Reservation : IDisposable
    {
        private readonly SharedReservation _shared;
        private int _released;

        internal Reservation(PeerWorkQuota owner, PublicKey id, WorkKind kind, (string Protocol, int PacketType)? servingClass, NodeFilter.IpSubnetKey? source)
            => _shared = new(owner, id, kind, servingClass, source);

        private Reservation(SharedReservation shared) => _shared = shared;

        internal Reservation Retain()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            Reservation retained = new(_shared);
            _shared.Retain();
            return retained;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _shared.Release();
        }

        private sealed class SharedReservation(PeerWorkQuota owner, PublicKey id, WorkKind kind,
            (string Protocol, int PacketType)? servingClass, NodeFilter.IpSubnetKey? source)
        {
            private int _references = 1;

            internal void Retain()
            {
                int references;
                do
                {
                    references = Volatile.Read(ref _references);
                    ObjectDisposedException.ThrowIf(references == 0, this);
                } while (Interlocked.CompareExchange(ref _references, references + 1, references) != references);
            }

            internal void Release()
            {
                if (Interlocked.Decrement(ref _references) == 0) owner.Release(id, kind, servingClass, source);
            }
        }
    }
}
