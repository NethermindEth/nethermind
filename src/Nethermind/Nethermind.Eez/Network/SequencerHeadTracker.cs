// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Eez.Network;

internal enum SequencerHeadVerdict
{
    Accept,
    Ignore,
    Reject,
}

internal sealed class SequencerHeadTracker
{
    internal const int MaxSources = 8;
    internal const ulong MaxFutureMs = 10000;

    private readonly Dictionary<ulong, VerifiedSequencerHead> _sources = new(MaxSources);
    private readonly Lock _lock = new();

    internal SequencerHeadVerdict Observe(VerifiedSequencerHead head, ulong nowMs)
    {
        SequencerHeadClaim claim = head.Claim;
        if (claim.EpochMs > nowMs && claim.EpochMs - nowMs > MaxFutureMs) return SequencerHeadVerdict.Ignore;
        lock (_lock)
        {
            if (_sources.TryGetValue(claim.EpochMs, out VerifiedSequencerHead? previous))
            {
                if (claim.Counter < previous.Claim.Counter) return SequencerHeadVerdict.Ignore;
                if (claim.Counter == previous.Claim.Counter)
                    return claim.BlockHash == previous.Claim.BlockHash ? SequencerHeadVerdict.Ignore : SequencerHeadVerdict.Reject;
            }
            else if (_sources.Count == MaxSources)
            {
                ulong oldest = ulong.MaxValue;
                foreach (ulong epoch in _sources.Keys) oldest = Math.Min(oldest, epoch);
                if (claim.EpochMs < oldest) return SequencerHeadVerdict.Ignore;
                _sources.Remove(oldest);
            }
            _sources[claim.EpochMs] = head;
            return SequencerHeadVerdict.Accept;
        }
    }

    internal VerifiedSequencerHead[] Candidates()
    {
        lock (_lock)
        {
            VerifiedSequencerHead[] heads = new VerifiedSequencerHead[_sources.Count];
            _sources.Values.CopyTo(heads, 0);
            Array.Sort(heads, static (left, right) => right.Claim.EpochMs.CompareTo(left.Claim.EpochMs));
            return heads;
        }
    }
}
