// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>The global rate limit on leanSTARK verifications that proof-wrapper admission spends, ethereum/EIPs#12664's explicit
/// budget for direct mode-0 witnesses, applied to mode-1 proofs too.</summary>
/// <remarks>A token bucket of <see cref="VerificationsPerSecond"/>, which is also its burst. A wrapper the bucket cannot pay for
/// is deferred, never judged invalid. Per-peer shares come from the transport's round-robin validation queue.</remarks>
internal sealed class LeanStarkVerificationBudget(TimeProvider clock)
{
    /// <summary>leanSTARK verifications admitted per second, about one core at the measured ~10-15 ms each.</summary>
    public const int VerificationsPerSecond = 64;

    private readonly Lock _lock = new();
    private double _tokens = VerificationsPerSecond;
    private long _last = clock.GetTimestamp();

    /// <summary>The verifications validating <paramref name="wrapper"/> costs: each direct leanSTARK witness in mode 0, the one
    /// recursive proof in mode 1.</summary>
    public static int Cost(MempoolWrapper wrapper) => wrapper.Mode == MempoolWrapper.ModeRecursive
        ? 1
        : Eip8288Dependencies.CountLeanStark(wrapper.Deps);

    /// <summary>Spends <paramref name="cost"/> verifications if the bucket holds them.</summary>
    public bool TrySpend(int cost)
    {
        lock (_lock)
        {
            long now = clock.GetTimestamp();
            _tokens = Math.Min(VerificationsPerSecond, _tokens + clock.GetElapsedTime(_last, now).TotalSeconds * VerificationsPerSecond);
            _last = now;
            if (cost > _tokens) return false;
            _tokens -= cost;
            return true;
        }
    }
}
