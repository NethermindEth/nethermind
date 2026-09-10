// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>Round timeout: <c>requestTimeoutSeconds * 2^round</c>, saturating at <see cref="MaxExpiry"/>.</summary>
/// <remarks>Besu saturates too, by casting the same power of two to a <c>long</c>.</remarks>
public sealed class BftRoundExpiryTimeCalculator(TimeSpan baseExpiryPeriod)
{
    public static readonly TimeSpan MaxExpiry = TimeSpan.FromDays(1);

    public TimeSpan CalculateRoundExpiry(ConsensusRoundIdentifier round)
    {
        double milliseconds = baseExpiryPeriod.TotalMilliseconds * Math.Pow(2, round.Round);
        return milliseconds >= MaxExpiry.TotalMilliseconds ? MaxExpiry : TimeSpan.FromMilliseconds(milliseconds);
    }
}

/// <summary>Fires <see cref="RoundExpiryEvent"/> when the current round runs out of time; one round at a time.</summary>
public sealed class RoundTimer(IBftEventQueue queue, BftRoundExpiryTimeCalculator expiryCalculator, IBftTimerScheduler scheduler, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<RoundTimer>();
    private readonly Lock _lock = new();
    private IDisposable? _current;

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _current is not null;
        }
    }

    public void CancelTimer()
    {
        lock (_lock)
        {
            _current?.Dispose();
            _current = null;
        }
    }

    public void StartTimer(ConsensusRoundIdentifier round)
    {
        lock (_lock)
        {
            TimeSpan expiry = expiryCalculator.CalculateRoundExpiry(round);
            IDisposable? previous = _current;
            _current = scheduler.Schedule(() => queue.Add(new RoundExpiryEvent(round)), expiry);
            previous?.Dispose();
            if (round.Round >= 2 && _logger.IsInfo)
            {
                _logger.Info($"Moved to round {round.Round} which will expire in {expiry.TotalSeconds:F0} seconds");
            }
        }
    }
}

/// <summary>
/// Fires <see cref="BlockTimerExpiryEvent"/> when the next block may be proposed, and tracks the
/// empty-block wait that follows when there is nothing worth proposing.
/// </summary>
/// <remarks>
/// The expiry is <c>parentTimestamp + blockPeriodSeconds</c>; when the period grows at the next fork the
/// longer of the two is used so the produced block still validates. The test-only millisecond period
/// counts from now instead. Mirrors Besu's <c>BlockTimer</c>.
/// </remarks>
public sealed class BlockTimer(IBftEventQueue queue, QbftForksSchedule forksSchedule, IBftTimerScheduler scheduler, ITimestamper clock, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlockTimer>();
    private readonly Lock _lock = new();
    private IDisposable? _current;
    private long _blockPeriodSeconds;
    private long _emptyBlockPeriodSeconds;
    private long _emptyBlockWaitStartedMillis;
    private long _emptyBlockPeriodExpiryMillis;

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _current is not null;
        }
    }

    public long BlockPeriodSeconds
    {
        get
        {
            lock (_lock) return _blockPeriodSeconds;
        }
    }

    public long EmptyBlockPeriodSeconds
    {
        get
        {
            lock (_lock) return _emptyBlockPeriodSeconds;
        }
    }

    /// <summary>Seconds the node has deliberately not been proposing because the chain is idle; 0 when producing normally.</summary>
    public long EmptyBlockWaitSeconds
    {
        get
        {
            long started = Volatile.Read(ref _emptyBlockWaitStartedMillis);
            return started == 0 ? 0 : Math.Max(0, (clock.UnixTime.MillisecondsLong - started) / 1000);
        }
    }

    /// <summary>Unix time in milliseconds at which the current empty block wait ends, or 0 when not waiting.</summary>
    public long EmptyBlockPeriodExpiryMillis => Volatile.Read(ref _emptyBlockPeriodExpiryMillis);

    public void CancelTimer()
    {
        lock (_lock)
        {
            _current?.Dispose();
            _current = null;
        }
    }

    public void StartTimer(ConsensusRoundIdentifier round, ulong parentTimestamp)
    {
        lock (_lock)
        {
            CancelTimer();
            QbftConfigSnapshot current = forksSchedule.GetFork(round.Sequence, parentTimestamp);
            long blockPeriodSeconds = current.BlockPeriodSeconds;
            long nextBlockPeriodSeconds = forksSchedule.GetFork(round.Sequence, parentTimestamp + (ulong)blockPeriodSeconds).BlockPeriodSeconds;
            // When the period grows at the next fork this block must already honour the longer one, or it fails validation.
            if (nextBlockPeriodSeconds > blockPeriodSeconds)
            {
                blockPeriodSeconds = nextBlockPeriodSeconds;
            }

            long expiryTime;
            if (current.XBlockPeriodMilliseconds > 0)
            {
                expiryTime = clock.UnixTime.MillisecondsLong + current.XBlockPeriodMilliseconds;
                if (_logger.IsWarn) _logger.Warn($"Test-mode only xblockperiodmilliseconds has been set to {current.XBlockPeriodMilliseconds} millisecond blocks. Do not use in a production system.");
            }
            else
            {
                expiryTime = (long)parentTimestamp * 1000 + blockPeriodSeconds * 1000;
            }

            _blockPeriodSeconds = blockPeriodSeconds;
            _emptyBlockPeriodSeconds = current.EmptyBlockPeriodSeconds;
            // A new height starts, so any previous empty-block wait is over.
            Volatile.Write(ref _emptyBlockWaitStartedMillis, 0);
            Volatile.Write(ref _emptyBlockPeriodExpiryMillis, 0);
            StartTimerAt(round, expiryTime);
        }
    }

    /// <summary>Whether the empty block period after <paramref name="parentTimestamp"/> has passed at <paramref name="currentTimeMillis"/>.</summary>
    public bool CheckEmptyBlockExpired(ulong parentTimestamp, long currentTimeMillis)
    {
        long expiry = ((long)parentTimestamp + EmptyBlockPeriodSeconds) * 1000;
        bool expired = currentTimeMillis > expiry;
        if (_logger.IsDebug) _logger.Debug(expired ? "Empty Block expired" : "Empty Block NOT expired");
        return expired;
    }

    /// <summary>Re-arms the timer to the earlier of the empty block period end and one more block period from now.</summary>
    public void ResetTimerForEmptyBlock(ConsensusRoundIdentifier round, ulong parentTimestamp, long currentTimeMillis)
    {
        lock (_lock)
        {
            long emptyBlockExpiry = ((long)parentTimestamp + _emptyBlockPeriodSeconds) * 1000;
            long nextBlockPeriodExpiry = currentTimeMillis + _blockPeriodSeconds * 1000;
            if (Volatile.Read(ref _emptyBlockWaitStartedMillis) == 0)
            {
                Volatile.Write(ref _emptyBlockWaitStartedMillis, currentTimeMillis);
            }

            Volatile.Write(ref _emptyBlockPeriodExpiryMillis, emptyBlockExpiry);
            StartTimerAt(round, Math.Min(emptyBlockExpiry, nextBlockPeriodExpiry));
        }
    }

    private void StartTimerAt(ConsensusRoundIdentifier round, long expiryTimeMillis)
    {
        _current?.Dispose();
        _current = null;
        long now = clock.UnixTime.MillisecondsLong;
        if (expiryTimeMillis > now)
        {
            _current = scheduler.Schedule(() => queue.Add(new BlockTimerExpiryEvent(round)), TimeSpan.FromMilliseconds(expiryTimeMillis - now));
        }
        else
        {
            queue.Add(new BlockTimerExpiryEvent(round));
        }
    }
}
