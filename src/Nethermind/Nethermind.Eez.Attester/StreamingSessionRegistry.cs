// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingSessionRegistry : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<ByteString, Entry> _sessions = [];
    private readonly StreamingRegistryLimits _limits;
    private readonly StreamingSessionLimits _sessionLimits;
    private readonly NativeStreamingProfile? _nativeProfile;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly long _reservation;
    private long _reservedBytes;
    private bool _disposed;
    internal StreamingProtocolRules Rules { get; }

    internal StreamingSessionRegistry(StreamingRegistryLimits limits, StreamingSessionLimits sessionLimits, TimeProvider time,
        StreamingSettlementProfile profile = StreamingSettlementProfile.Rollup0Calldata, NativeStreamingProfile? nativeProfile = null)
    {
        sessionLimits.Validate();
        limits.Validate();
        if (nativeProfile is not null && profile != StreamingSettlementProfile.RollupXNative)
            throw new ArgumentException("Native session profile does not match its execution configuration.", nameof(nativeProfile));
        Rules = new StreamingProtocolRules(profile, HasNativeHistory: nativeProfile is not null);
        _nativeProfile = nativeProfile;
        if (sessionLimits.MaxBytes > long.MaxValue / Rules.ReservationCopies) throw new ArgumentOutOfRangeException(nameof(sessionLimits));
        _reservation = sessionLimits.MaxBytes * Rules.ReservationCopies;
        if (_reservation > limits.MaxReservedBytes) throw new ArgumentOutOfRangeException(nameof(limits));
        _limits = limits;
        _sessionLimits = sessionLimits;
        _time = time;
        _timer = time.CreateTimer(static registry => ((StreamingSessionRegistry)registry!).Sweep(), this,
            limits.Retention, limits.Retention);
    }

    internal StreamingSession Begin(string authenticatedOwner, Begin begin, int frameBytes = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(authenticatedOwner);
        if (authenticatedOwner.Length > 256) throw new ArgumentOutOfRangeException(nameof(authenticatedOwner));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SweepCore();
            StreamingSession session = new(authenticatedOwner, begin, _sessionLimits, Rules, frameBytes, _nativeProfile);
            int owned = 0;
            foreach (Entry entry in _sessions.Values)
                if (StringComparer.Ordinal.Equals(entry.Owner, authenticatedOwner)) owned++;
            if (_sessions.Count >= _limits.MaxSessions || owned >= _limits.MaxSessionsPerOwner ||
                _reservation > _limits.MaxReservedBytes - _reservedBytes)
            {
                if (!Rules.EvictsDetachedSessions || !EvictDetached(authenticatedOwner))
                    throw Error(StatusCode.ResourceExhausted, "session registry limit exceeded");
            }
            _sessions.Add(session.Id, new Entry(session, authenticatedOwner, _time.GetTimestamp()));
            _reservedBytes += _reservation;
            return session;
        }
    }

    internal (StreamingSession Session, SessionBinding Binding) Resume(string authenticatedOwner, ByteString id, ulong rollupId)
    {
        (StreamingSession session, SessionBinding binding, _) = ResumeAttachment(authenticatedOwner, id, rollupId);
        return (session, binding);
    }

    internal (StreamingSession Session, SessionBinding Binding, Task Revoked) ResumeAttachment(string authenticatedOwner, ByteString id, ulong rollupId)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SweepCore();
            if (!_sessions.TryGetValue(id, out Entry? entry) || !StringComparer.Ordinal.Equals(entry.Owner, authenticatedOwner) ||
                entry.Session.RollupId != rollupId)
                throw Error(StatusCode.NotFound, "session unavailable");
            (SessionBinding binding, Task revoked) = entry.Session.ResumeAttachment(authenticatedOwner);
            entry.LastUsed = _time.GetTimestamp();
            return (entry.Session, binding, revoked);
        }
    }

    internal void Touch(StreamingSession session, SessionBinding binding)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryGetValue(session.Id, out Entry? entry) || !ReferenceEquals(entry.Session, session))
                throw Error(StatusCode.NotFound, "session unavailable");
            long now = _time.GetTimestamp();
            if (_time.GetElapsedTime(entry.LastUsed, now) >= _limits.Retention) session.Retire();
            session.CheckCurrent(binding);
            entry.LastUsed = now;
        }
    }

    internal void Detach(StreamingSession session, SessionBinding binding)
    {
        lock (_lock)
        {
            if (!_disposed && _sessions.TryGetValue(session.Id, out Entry? entry) && ReferenceEquals(entry.Session, session) && session.Detach(binding))
                entry.LastUsed = _time.GetTimestamp();
        }
    }

    private bool EvictDetached(string owner)
    {
        Entry? oldest = null;
        foreach (Entry entry in _sessions.Values)
            if (StringComparer.Ordinal.Equals(entry.Owner, owner) && entry.Session.CanEvict &&
                (oldest is null || entry.LastUsed < oldest.LastUsed)) oldest = entry;
        if (oldest is null || !oldest.Session.RetireDetached()) return false;
        _sessions.Remove(oldest.Session.Id);
        _reservedBytes -= _reservation;
        return true;
    }

    internal void Sweep()
    {
        lock (_lock)
        {
            if (!_disposed) SweepCore();
        }
    }

    private void SweepCore()
    {
        long now = _time.GetTimestamp();
        foreach (KeyValuePair<ByteString, Entry> pair in _sessions)
        {
            Entry entry = pair.Value;
            if (_time.GetElapsedTime(entry.LastUsed, now) >= _limits.Retention) entry.Session.Retire();
            if (entry.Session.CanRelease)
            {
                _sessions.Remove(pair.Key);
                _reservedBytes -= _reservation;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Entry entry in _sessions.Values) entry.Session.Retire();
            _sessions.Clear();
            _reservedBytes = 0;
        }
        _timer.Dispose();
    }

    private static RpcException Error(StatusCode code, string message) => new(new Status(code, message));
    private sealed class Entry(StreamingSession session, string owner, long lastUsed)
    {
        internal StreamingSession Session { get; } = session;
        internal string Owner { get; } = owner;
        internal long LastUsed { get; set; } = lastUsed;
    }
}

internal sealed record StreamingRegistryLimits(int MaxSessions, int MaxSessionsPerOwner, long MaxReservedBytes, TimeSpan Retention)
{
    internal void Validate()
    {
        if (MaxSessions <= 0 || MaxSessionsPerOwner <= 0 || MaxReservedBytes <= 0 || Retention < TimeSpan.FromMilliseconds(1) ||
            Retention.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(MaxSessions));
    }
}
