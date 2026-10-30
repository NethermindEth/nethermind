// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Sequencer;

namespace Nethermind.Eez.Proving;

/// <summary>
/// Asks one attester until it signs, the budget runs out or it refuses the batch. A pinned batch must be proven
/// <see cref="RollupTiming.ProofTimeMs"/> plus the submission slack before its L1 block, so no attempt starts after
/// that and each gets the proof time; an unpinned batch has the proof time for all attempts together. Retries back off
/// from 100 ms, doubling up to 1 s, with 20% jitter so attesters restarting together are not hit in step.
/// </summary>
public sealed class ProveRetry(RollupTiming timing, ITimestamper clock)
{
    internal const int InitialBackoffMs = 100;
    internal const int MaxBackoffMs = 1_000;

    /// <exception cref="ProveException">The attester refused the batch, or did not sign it within the budget.</exception>
    public async Task<byte[]> Prove(IAttester attester, ProveRequest request, BundleTarget target, CancellationToken token)
    {
        DateTimeOffset now = clock.UtcNowOffset;
        DateTimeOffset deadline = now + Budget(target, now);
        for (int attempt = 1; ; attempt++)
        {
            ProveException failure;
            TimeSpan attemptBudget = target.IsPinned ? TimeSpan.FromMilliseconds(timing.ProofTimeMs) : deadline - clock.UtcNowOffset;
            using (CancellationTokenSource attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                attemptTimeout.CancelAfter(attemptBudget);
                try
                {
                    return await attester.Prove(request, attemptTimeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    failure = new ProveException(ProveFailureKind.Retryable, $"The proof attempt exceeded its {attemptBudget.TotalMilliseconds:F0} ms budget.");
                }
                catch (ProveException e) when (e.Kind == ProveFailureKind.Retryable)
                {
                    failure = e;
                }
            }

            TimeSpan backoff = Backoff(attempt, Random.Shared.Next());
            if (clock.UtcNowOffset + backoff >= deadline)
            {
                throw failure;
            }

            await Task.Delay(backoff, token);
            if (clock.UtcNowOffset >= deadline)
            {
                throw failure;
            }
        }
    }

    /// <summary>How long the attempts may take in all.</summary>
    /// <exception cref="ProveException">A pinned batch is already past the last moment its proof could start.</exception>
    internal TimeSpan Budget(BundleTarget target, DateTimeOffset now)
    {
        if (!target.IsPinned)
        {
            return TimeSpan.FromMilliseconds(timing.ProofTimeMs);
        }

        DateTimeOffset cutoff = DateTimeOffset.FromUnixTimeSeconds((long)target.Timestamp)
            - TimeSpan.FromMilliseconds(timing.SubmissionSlackMs + (double)timing.ProofTimeMs);
        return now <= cutoff
            ? cutoff - now
            : throw new ProveException(ProveFailureKind.Retryable, $"The proof for L1 block {target.Block} can no longer start in time.");
    }

    /// <summary>How long to keep collecting proofs past the threshold: <paramref name="grace"/>, cut to what is left before a pinned batch must be submitted.</summary>
    internal TimeSpan Grace(BundleTarget target, TimeSpan grace)
    {
        if (!target.IsPinned)
        {
            return grace;
        }

        TimeSpan left = DateTimeOffset.FromUnixTimeSeconds((long)target.Timestamp) - TimeSpan.FromMilliseconds(timing.SubmissionSlackMs) - clock.UtcNowOffset;
        return left <= TimeSpan.Zero ? TimeSpan.Zero : left < grace ? left : grace;
    }

    internal static TimeSpan Backoff(int failedAttempt, int seed)
    {
        int doublings = Math.Min(failedAttempt - 1, 10);
        int baseMs = Math.Min(InitialBackoffMs << doublings, MaxBackoffMs);
        int jitter = baseMs / 5;
        return TimeSpan.FromMilliseconds(baseMs - jitter + (int)((uint)seed % (uint)(2 * jitter + 1)));
    }
}
