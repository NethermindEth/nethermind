// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

internal sealed class CellResponseHistory
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan EvidenceWindow = TimeSpan.FromMinutes(5);
    private readonly Sample[] _samples = new Sample[16];
    private readonly Lock _lock = new();
    private int _count;
    private int _next;
    private long? _lastUseful;

    internal bool IsBackedOff(TimeProvider clock)
    {
        lock (_lock)
        {
            (int failures, int useful) = Count(clock, Window);
            return failures + useful >= 4 && failures > useful;
        }
    }

    internal (int Failures, int Useful) Record(ValueHash256 hash, bool useful, TimeProvider clock)
    {
        lock (_lock)
        {
            long now = clock.GetTimestamp();
            if (useful)
            {
                _lastUseful = now;
                for (int i = 0; i < _count; i++)
                    if (_samples[i].Useful && _samples[i].Hash == hash && clock.GetElapsedTime(_samples[i].At, now) < Window)
                        return Count(clock, EvidenceWindow);
            }
            _samples[_next] = new(hash, now, useful);
            _next = (_next + 1) % _samples.Length;
            _count = Math.Min(_count + 1, _samples.Length);
            return Count(clock, EvidenceWindow);
        }
    }

    internal bool HasRecentUseful(TimeProvider clock)
    {
        lock (_lock)
            return _lastUseful is { } at && clock.GetElapsedTime(at, clock.GetTimestamp()) < EvidenceWindow;
    }

    private (int Failures, int Useful) Count(TimeProvider clock, TimeSpan window)
    {
        int failures = 0;
        int useful = 0;
        long now = clock.GetTimestamp();
        for (int i = 0; i < _count; i++)
        {
            Sample sample = _samples[i];
            if (clock.GetElapsedTime(sample.At, now) >= window) continue;
            if (sample.Useful) useful++;
            else failures++;
        }
        return (failures, useful);
    }

    private readonly record struct Sample(ValueHash256 Hash, long At, bool Useful);
}
