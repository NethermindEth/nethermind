// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>Bounded dial outcomes keyed by endpoint, shared by discovery and admission.</summary>
internal sealed class PeerDialHistory(ITimestamper clock, int capacity = 8192)
{
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Outcome> _outcomes = new(StringComparer.Ordinal);
    private long _sequence;

    private readonly record struct Outcome(int Failures, DateTime RetryAt, long Sequence);

    internal int Count { get { lock (_lock) return _outcomes.Count; } }

    internal bool CanDial(string address)
    {
        lock (_lock)
        {
            return !_outcomes.TryGetValue(address, out Outcome outcome) || clock.UtcNow >= outcome.RetryAt;
        }
    }

    internal int Quality(string address)
    {
        lock (_lock)
        {
            return !_outcomes.TryGetValue(address, out Outcome outcome) ? 0 : outcome.Failures == 0 ? 1 : -outcome.Failures;
        }
    }

    internal void Record(string address, bool connected)
    {
        lock (_lock)
        {
            _outcomes.TryGetValue(address, out Outcome previous);
            int failures = connected ? 0 : Math.Min(previous.Failures + 1, 7);
            long delay = connected ? 0 : Math.Min(InitialBackoff.Ticks << (failures - 1), MaximumBackoff.Ticks);
            // Endpoint jitter prevents repeated failures from all becoming eligible together.
            delay = Math.Min(MaximumBackoff.Ticks, delay + delay / 100 * ((uint)StringComparer.Ordinal.GetHashCode(address) % 21));
            _outcomes[address] = new Outcome(failures, clock.UtcNow + TimeSpan.FromTicks(delay), ++_sequence);
            if (_outcomes.Count > capacity)
            {
                string? oldest = null;
                long sequence = long.MaxValue;
                foreach (KeyValuePair<string, Outcome> entry in _outcomes)
                {
                    if (entry.Value.Sequence < sequence)
                    {
                        oldest = entry.Key;
                        sequence = entry.Value.Sequence;
                    }
                }

                if (oldest is not null) _outcomes.Remove(oldest);
            }
        }
    }
}
