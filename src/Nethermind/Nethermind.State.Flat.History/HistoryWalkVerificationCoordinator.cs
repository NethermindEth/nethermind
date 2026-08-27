// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Runs the one-shot every-block history proof when <c>FlatDb.HistoryVerifyEveryBlock</c> is on: waits for a
/// contiguous watermark to exist, then walks <see cref="HistoryWalkVerifier"/> over <c>[0, watermark]</c> in the
/// background and reports the verdict. Its verdict changes nothing about serving - a mismatch is an operator's
/// loud signal, not an automatic un-publish.
/// </summary>
public sealed class HistoryWalkVerificationCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultPollDelay = TimeSpan.FromSeconds(30);
    private const int MismatchesLogged = 8;

    private readonly HistoryAvailability _availability;
    private readonly IFlatDbConfig _config;
    private readonly HistoryWalkVerifier? _verifier;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollDelay;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;
    private object? _verdict;
    private bool _disposed;

    public HistoryWalkVerificationCoordinator(
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        IHistoryHeaderSource headers,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        IFlatDbConfig config,
        ILogManager logManager,
        TimeSpan? pollDelay = null)
    {
        _availability = availability;
        _config = config;
        _logger = logManager.GetClassLogger<HistoryWalkVerificationCoordinator>();
        _pollDelay = pollDelay ?? DefaultPollDelay;

        Started = config.HistoryVerifyEveryBlock;

        // Constructed only when the flag is on: the verifier refuses a windowed database in its constructor, and
        // that refusal must fire exactly when the operator asked for a verification the mode cannot deliver -
        // never on a windowed node that left the flag alone.
        _verifier = Started
            ? new HistoryWalkVerifier(db, history, headers, rowFormat, logManager, config.HistoryVerifyMaxRows)
            : null;
    }

    /// <summary>Launches the background verification. Called by the startup step, never from the constructor, so
    /// resolving the singleton has no side effects. No-op when the flag is off or already started.</summary>
    public void Start()
    {
        if (!Started || !ReferenceEquals(_loop, Task.CompletedTask)) return;
        _loop = RunAsync();
    }

    /// <summary>Whether this instance actually started its background verification - false means
    /// <c>FlatDb.HistoryVerifyEveryBlock</c> is off and this coordinator is a permanent no-op.</summary>
    public bool Started { get; }

    /// <summary>The completed run's verdict, or <c>null</c> while none has finished.</summary>
    public HistoryWalkVerdict? LastVerdict => _verdict is HistoryWalkVerdict verdict ? verdict : null;

    private async Task RunAsync()
    {
        CancellationToken token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_availability.TryGetWatermark(out ulong watermark) && watermark > 0)
                {
                    int segments = _config.HistoryVerifySegments > 0
                        ? _config.HistoryVerifySegments
                        : Math.Max(1, Environment.ProcessorCount / 2);

                    if (_logger.IsInfo) _logger.Info(
                        $"History walk verification starting: every block in [0, {watermark}] against this node's own headers, {segments} segments.");

                    long startedAt = Stopwatch.GetTimestamp();
                    HistoryWalkVerdict verdict = await Task.Run(() => _verifier!.VerifyRangeParallel(0, watermark, segments, token), token);
                    _verdict = verdict;
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

                    if (verdict.Verified)
                    {
                        if (_logger.IsInfo) _logger.Info(
                            $"History walk verification PASSED: {verdict.BlocksCompared} blocks rebuilt from rows and matched against headers in {elapsed}.");
                    }
                    else if (_logger.IsError)
                    {
                        string sample = string.Join(", ", verdict.Mismatches.Take(MismatchesLogged).Select(m => $"{m.Block}:{m.Kind}"));
                        _logger.Error(
                            $"History walk verification FAILED after {elapsed}: {verdict.Mismatches.Count} mismatches over {verdict.BlocksCompared} compared blocks. " +
                            $"First {Math.Min(verdict.Mismatches.Count, MismatchesLogged)}: {sample}. " +
                            "The history rows on this node do not reproduce this node's own headers; do not treat its historical answers as canonical.");
                    }

                    return;
                }

                await Task.Delay(_pollDelay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (InvalidConfigurationException e)
        {
            if (_logger.IsWarn) _logger.Warn($"History walk verification declined before it started: {e.Message}");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("History walk verification crashed; the archive's content is UNVERIFIED, not disproven.", e);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _cts.Cancel();
        try
        {
            _loop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
