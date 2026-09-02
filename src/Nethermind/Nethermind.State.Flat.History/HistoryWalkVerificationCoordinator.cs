// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Walk;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Runs the one-shot every-block history proof when <c>FlatDb.HistoryVerifyEveryBlock</c> is on: waits for a
/// contiguous watermark to exist, then walks <see cref="HistoryWalkVerifier"/> over <c>[0, watermark]</c> in the
/// background and reports the verdict. Its verdict changes nothing about serving - a mismatch is an operator's
/// loud signal, not an automatic un-publish.
/// </summary>
public sealed class HistoryWalkVerificationCoordinator : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollDelay = TimeSpan.FromSeconds(30);
    private const int MismatchesLogged = 8;

    private readonly HistoryAvailability _availability;
    private readonly IFlatDbConfig _config;
    private readonly Func<long, HistoryWalkVerifier>? _verifierFactory;
    private readonly ArchiveProofRetrofit? _retrofit;
    private readonly CommitmentMetadata _metadata;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollDelay;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;
    private HistoryWalkVerdict? _verdict;
    private bool _disposed;

    public HistoryWalkVerificationCoordinator(
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        IHistoryHeaderSource headers,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        IFlatDbConfig config,
        ArchiveProofRetrofit retrofit,
        CommitmentMetadata metadata,
        ILogManager logManager)
        : this(db, history, headers, availability, rowFormat, config, retrofit, metadata, logManager, pollDelay: null)
    {
    }

    internal HistoryWalkVerificationCoordinator(
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        IHistoryHeaderSource headers,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        IFlatDbConfig config,
        ArchiveProofRetrofit retrofit,
        CommitmentMetadata metadata,
        ILogManager logManager,
        TimeSpan? pollDelay)
    {
        _availability = availability;
        _config = config;
        _metadata = metadata;
        _logger = logManager.GetClassLogger<HistoryWalkVerificationCoordinator>();
        _pollDelay = pollDelay ?? DefaultPollDelay;

        Started = config.HistoryVerifyEveryBlock;
        _retrofit = retrofit.Enabled ? retrofit : null;

        // Constructed only when the flag is on: the verifier refuses a windowed database in its constructor, and
        // that refusal must fire exactly when the operator asked for a verification the mode cannot deliver -
        // never on a windowed node that left the flag alone.
        if (Started)
        {
            HistoryWalkVerifier.RequireUnwindowed(rowFormat);
            _verifierFactory = rows => new HistoryWalkVerifier(db, history, headers, rowFormat, logManager, rows, _retrofit);
        }
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
    public HistoryWalkVerdict? LastVerdict => Volatile.Read(ref _verdict);

    /// <summary>The running verification, completed once a verdict has published; tests await this instead of polling.</summary>
    internal Task VerificationLoop => _loop;

    private async Task RunAsync()
    {
        CancellationToken token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_availability.TryGetWatermark(out ulong watermark) && watermark > 0)
                {
                    WalkResources resources = WalkResources.Resolve(_config);
                    int workers = resources.Workers;
                    HistoryWalkVerifier verifier = _verifierFactory!(resources.RowsPerPartition);
                    if (_logger.IsInfo) _logger.Info($"History walk sized for this machine: {resources}.");

                    ulong from = 0;
                    ulong to = watermark;
                    if (_metadata.TryGetWalkInProgress(out ulong pendingFrom, out ulong pendingTo))
                    {
                        from = pendingFrom;
                        to = pendingTo;
                        if (_logger.IsInfo) _logger.Info($"History walk verification resuming the interrupted run over [{from}, {to}].");
                    }

                    _retrofit?.Prepare();
                    while (true)
                    {
                        if (_logger.IsInfo) _logger.Info(
                            $"History walk verification starting: every block in [{from}, {to}] against this node's own headers, {workers} workers.");

                        long startedAt = Stopwatch.GetTimestamp();
                        ulong rangeFrom = from;
                        ulong rangeTo = to;
                        HistoryWalkVerdict verdict = await Task.Run(() => verifier.VerifyRangeParallel(rangeFrom, rangeTo, workers, token), token);
                        Volatile.Write(ref _verdict, verdict);
                        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

                        if (!verdict.Verified)
                        {
                            LogFailure(verdict, elapsed);
                            return;
                        }

                        _retrofit?.PublishCoverage(from, to);
                        if (_logger.IsInfo) _logger.Info(
                            $"History walk verification PASSED: {verdict.BlocksCompared} blocks rebuilt from rows and matched against headers in {elapsed}.");

                        if (!_availability.TryGetWatermark(out ulong latest) || latest <= to) return;

                        ulong granularity = _retrofit?.WindowGranularity ?? 1;
                        from = to - to % granularity;
                        to = latest;
                        if (_logger.IsInfo) _logger.Info($"History walk verification continuing over the blocks captured meanwhile, [{from}, {to}].");
                    }
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

    private void LogFailure(HistoryWalkVerdict verdict, TimeSpan elapsed)
    {
        if (!_logger.IsError) return;

        StringBuilder sample = new();
        for (int i = 0; i < verdict.Mismatches.Count && i < MismatchesLogged; i++)
        {
            if (i > 0) sample.Append(", ");
            sample.Append(verdict.Mismatches[i].Block).Append(':').Append(verdict.Mismatches[i].Kind);
        }

        _logger.Error(
            $"History walk verification FAILED after {elapsed}: {verdict.Mismatches.Count} mismatches over {verdict.BlocksCompared} compared blocks. " +
            $"First {Math.Min(verdict.Mismatches.Count, MismatchesLogged)}: {sample}. " +
            "The history rows on this node do not reproduce this node's own headers; do not treat its historical answers as canonical.");
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        await _cts.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
