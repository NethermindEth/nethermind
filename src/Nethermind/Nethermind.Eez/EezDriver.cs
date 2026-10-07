// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.Eez.Config;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Sequencer;
using Nethermind.Logging;

namespace Nethermind.Eez;

/// <summary>
/// The one loop that drives the node's engine for the rollup: it follows L1 and, on a sequencer, produces blocks on
/// the slot schedule, one step after the other, so derivation and sequencing never race for the head. The sequencer
/// runs only once the follower has caught up with L1, since a batch posted from a stale cursor would revert. It wakes
/// for the next L1 poll or the next thing the sequencer has due, whichever comes first.
/// </summary>
public sealed class EezDriver(
    EezFollower follower,
    IEezSequencer? sequencer,
    IEezConfig config,
    ITimestamper clock,
    IProcessExitSource processExit,
    ILogManager logManager) : IStoppableService, IAsyncDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<EezDriver>();
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _running;
    private int _stopped;

    public string Description => "EEZ driver";

    /// <summary>Whether the loop sequences the rollup as well as following it.</summary>
    internal bool Sequences => sequencer is not null;

    /// <summary>Starts driving; the returned task runs until the node stops.</summary>
    /// <remarks>Every failure is handled inside: transient ones are retried, the rest stop the node.</remarks>
    public Task Start() => _running ??= Run(_cancellation.Token);

    private async Task Run(CancellationToken token)
    {
        try
        {
            while (!await follower.TryBoot(token))
            {
                await Task.Delay(config.L1PollingIntervalMs, token);
            }

            DateTimeOffset nextPoll = default;
            while (true)
            {
                (nextPoll, DateTimeOffset wake) = await Step(nextPoll, token);
                TimeSpan wait = wake - clock.UtcNowOffset;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (EezFollowerException e)
        {
            if (_logger.IsError) _logger.Error($"EEZ driver stopped: {e.Message}");
            processExit.Exit(ExitCodes.InvalidBlock);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("EEZ driver failed.", e);
            processExit.Exit(ExitCodes.GeneralError);
        }
    }

    /// <summary>Polls L1 when it is due, then lets the sequencer act once the follower is caught up.</summary>
    /// <returns>When L1 is next polled, and when the loop next has anything to do.</returns>
    internal async Task<(DateTimeOffset NextPoll, DateTimeOffset Wake)> Step(DateTimeOffset nextPoll, CancellationToken token)
    {
        DateTimeOffset now = clock.UtcNowOffset;
        bool caughtUp = true;
        if (now >= nextPoll)
        {
            caughtUp = !await follower.Poll(token);
            nextPoll = caughtUp ? now + TimeSpan.FromMilliseconds(config.L1PollingIntervalMs) : now;
        }

        if (sequencer is null || !caughtUp)
        {
            return (nextPoll, nextPoll);
        }

        try
        {
            DateTimeOffset due = await sequencer.Advance(follower.Heads, follower.LatestL1, token);
            return (nextPoll, due < nextPoll ? due : nextPoll);
        }
        catch (Exception e) when (EezFollower.IsTransient(e, token))
        {
            if (_logger.IsWarn) _logger.Warn($"EEZ sequencer retries on the next step: {e.Message}");
            return (nextPoll, nextPoll);
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        await _cancellation.CancelAsync();
        if (_running is not null)
        {
            await _running;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cancellation.Dispose();
    }
}
