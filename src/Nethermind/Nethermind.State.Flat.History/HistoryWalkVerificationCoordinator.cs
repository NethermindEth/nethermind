// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;
using Nethermind.Core.Crypto;
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

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly HistoryAvailability _availability;
    private readonly IFlatDbConfig _config;
    private readonly Func<long, HistoryWalkVerifier>? _verifierFactory;
    private readonly ArchiveProofRetrofit? _retrofit;
    private readonly CommitmentMetadata _metadata;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollDelay;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _verifyEveryBlock;
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
        _history = history;
        _availability = availability;
        _config = config;
        _metadata = metadata;
        _logger = logManager.GetClassLogger<HistoryWalkVerificationCoordinator>();
        _pollDelay = pollDelay ?? DefaultPollDelay;

        _verifyEveryBlock = config.HistoryVerifyEveryBlock;
        _retrofit = retrofit.Enabled ? retrofit : null;
        // A history started at a snap pivot builds its commitments with the walk, from the pivot's image up.
        Started = _verifyEveryBlock || (_retrofit is not null && !rowFormat.IsV3);

        // Constructed only when the flag is on: the verifier refuses a windowed database in its constructor, and
        // that refusal must fire exactly when the operator asked for a verification the mode cannot deliver -
        // never on a windowed node that left the flag alone.
        if (Started)
        {
            HistoryWalkVerifier.RequireUnwindowed(rowFormat);
            IHistoryHeaderSource walkHeaders = new PivotImageHeaderSource(headers, availability);
            _verifierFactory = rows => new HistoryWalkVerifier(db, history, walkHeaders, rowFormat, logManager, rows, _retrofit, _metadata);
        }
    }

    /// <summary>Launches the background verification. Called by the startup step, never from the constructor, so
    /// resolving the singleton has no side effects. No-op when the flag is off or already started.</summary>
    public void Start()
    {
        if (!Started)
        {
            AbandonLeftoverWalk();
            return;
        }

        // Started only to build a snap pivot's commitments: a checkpoint without a pivot image is from a run with the
        // flag on, and this run will never resume it. A history that already has a watermark but no image started at
        // genesis, so no image will ever come and there is nothing to wait for.
        if (!_verifyEveryBlock && !_availability.TryGetPivotImageBlock(out _))
        {
            AbandonLeftoverWalk();
            if (_availability.TryGetWatermark(out ulong watermark) && watermark > 0) return;
        }

        if (!ReferenceEquals(_loop, Task.CompletedTask)) return;
        _loop = RunAsync();
    }

    private void AbandonLeftoverWalk()
    {
        if (!_metadata.TryGetWalkInProgress(out ulong from, out ulong to)) return;

        DiscardWalk();
        _retrofit?.ResumeReclaim();
        if (_logger.IsInfo) _logger.Info($"History walk verification is off, so the run interrupted over [{from}, {to}] is abandoned: its checkpoint and scratch series are deleted and commitment reclaim no longer waits for it. Turning FlatDb.HistoryVerifyEveryBlock back on starts a new walk.");
    }

    private void DiscardWalk()
    {
        _metadata.ClearWalk(HistoryWalkRun.WorkItems);
        using SeriesWriter scratch = new(_history);
        scratch.DeleteAllScratch();
    }

    /// <summary>Whether this instance runs the walk: with <c>FlatDb.HistoryVerifyEveryBlock</c> on, or with archive
    /// proofs built on unwindowed history, where it builds the commitments of a snap pivot. False means a permanent
    /// no-op.</summary>
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
                if (_availability.TryGetWatermark(out ulong watermark) && watermark > 0
                    && (_verifyEveryBlock || _availability.TryGetPivotImageBlock(out _)))
                {
                    WalkResources resources = WalkResources.Resolve(_config);
                    int workers = resources.Workers;
                    HistoryWalkVerifier verifier = _verifierFactory!(resources.RowsPerPartition);
                    if (_logger.IsInfo) _logger.Info($"History walk sized for this machine: {resources}.");

                    _retrofit?.Prepare();
                    ulong from = _retrofit?.FirstBlockToBuild(watermark) ?? 0;
                    // A history started at a snap pivot has no rows below its image, so the walk starts there.
                    bool fromPivot = _availability.TryGetPivotImageBlock(out ulong imageBlock) && imageBlock >= from;
                    if (fromPivot) from = imageBlock;
                    ulong to = watermark;
                    if (fromPivot && _logger.IsInfo) _logger.Info(
                        $"History walk will cover [{from}, {to}]: history starts at the snap-sync pivot, whose state was recorded at block {from}; it builds the proof commitments from there.");
                    else if (from > 0 && _logger.IsInfo) _logger.Info(
                        $"History walk verification will cover [{from}, {to}] rather than the whole chain: only the most recent commitment epochs are kept, so the blocks below that are neither built nor served, and the walk is a fraction of the work.");

                    if (!_metadata.TryGetWalkInProgress(out _, out _) && _metadata.TryGetWalkVerified(out ulong verifiedFrom, out ulong verifiedTo) && verifiedFrom <= from && verifiedTo >= from
                        && (_retrofit is null || (_metadata.TryGetCoverage(out ulong builtFrom, out ulong builtTo) && builtFrom <= from && builtTo >= verifiedTo)))
                    {
                        if (verifiedTo >= to || TipCovers(verifiedTo + 1, to))
                        {
                            if (_logger.IsInfo) _logger.Info(
                                $"History walk verification has nothing to do: blocks [{from}, {verifiedTo}] were verified by an earlier run and the tip has committed the rest up to {to}.");
                            return;
                        }

                        ulong resumeGranularity = _retrofit?.WindowGranularity ?? 1;
                        from = Math.Max(from, (verifiedTo + 1) - (verifiedTo + 1) % resumeGranularity);
                        if (_logger.IsInfo) _logger.Info($"History walk verification continues from block {from}: blocks below it were verified by an earlier run.");
                    }

                    if (_metadata.TryGetWalkInProgress(out ulong pendingFrom, out ulong pendingTo))
                    {
                        if (TipCovers(pendingFrom, pendingTo) && VerifiedReaches(pendingFrom)
                            && (_retrofit is null || CoverageIncludes(from, pendingTo)))
                        {
                            DiscardWalk();
                            _retrofit?.ResumeReclaim();
                            if (_logger.IsInfo) _logger.Info(
                                $"History walk verification dropped its unfinished run over [{pendingFrom}, {pendingTo}]: the tip has committed those blocks itself, and a walk over them would scan the whole key space to find them.");
                            return;
                        }

                        string? restartReason = GetRestartReason(from, pendingFrom);
                        if (restartReason is not null)
                        {
                            DiscardWalk();
                            if (TryGetCompletedPrefix(from, out ulong completedTo))
                            {
                                if (completedTo >= to || TipCovers(completedTo + 1, to))
                                {
                                    _retrofit?.ResumeReclaim();
                                    if (_logger.IsWarn) _logger.Warn($"History walk discarded checkpoint [{pendingFrom}, {pendingTo}]: {restartReason}; the requested range [{from}, {to}] is already complete.");
                                    return;
                                }

                                ulong granularity = _retrofit?.WindowGranularity ?? 1;
                                ulong next = completedTo + 1;
                                from = Math.Max(from, next - next % granularity);
                            }
                            if (_logger.IsWarn) _logger.Warn($"History walk restarting over [{from}, {to}]: {restartReason} in the interrupted range [{pendingFrom}, {pendingTo}].");
                        }
                        else
                        {
                            from = pendingFrom;
                            to = pendingTo;
                            if (_logger.IsInfo) _logger.Info($"History walk verification resuming the interrupted run over [{from}, {to}].");
                        }
                    }
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
                        _retrofit?.ResweepDemotionFrom(from);

                        if (!verdict.Verified)
                        {
                            LogFailure(verdict, elapsed);
                            return;
                        }

                        if (_retrofit is not null && !_retrofit.PublishCoverage(from, to)) return;
                        if (_retrofit is null) _metadata.MarkWalkVerified(from, to);

                        _retrofit?.PruneBelow(to);
                        if (_logger.IsInfo) _logger.Info(
                            $"History walk verification PASSED: {verdict.BlocksCompared} blocks rebuilt from rows and matched against headers in {elapsed}.");

                        if (!_availability.TryGetWatermark(out ulong latest) || latest <= to) return;

                        if (TipCovers(to + 1, latest))
                        {
                            if (_logger.IsInfo) _logger.Info(
                                $"History walk verification is done at block {to}; the {latest - to} blocks captured meanwhile are already committed by the tip, so the walk does not run again. A walk over those blocks would scan the whole key space to find them.");
                            return;
                        }

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

    /// <summary>A history started at a snap pivot records the pivot's state at the start of its checkpoint window, so
    /// the rows rebuild the pivot's root at every block from there up to the pivot; those blocks are below the floor
    /// and never served, and the walk compares them against the pivot's own root. Both blocks are published together
    /// and an unwindowed floor never moves, so they are read once.</summary>
    private sealed class PivotImageHeaderSource(IHistoryHeaderSource inner, HistoryAvailability availability) : IHistoryHeaderSource
    {
        // A reference, so the walk's workers never see half of it.
        private ImageRange? _image;

        public ValueHash256? TryGetStateRoot(ulong block) =>
            TryGetImage(out ulong imageBlock, out ulong floor) && block >= imageBlock && block < floor
                ? inner.TryGetStateRoot(floor)
                : inner.TryGetStateRoot(block);

        private bool TryGetImage(out ulong imageBlock, out ulong floor)
        {
            ImageRange? range = Volatile.Read(ref _image);
            if (range is null && availability.TryGetPivotImageBlock(out ulong image) && availability.TryGetGlobalFloor(out ulong published))
            {
                range = new ImageRange(image, published);
                Volatile.Write(ref _image, range);
            }

            imageBlock = range?.ImageBlock ?? 0;
            floor = range?.Floor ?? 0;
            return range is not null;
        }

        private sealed record ImageRange(ulong ImageBlock, ulong Floor);
    }

    private string? GetRestartReason(ulong from, ulong pendingFrom)
    {
        if (!_metadata.TryGetWalkMode(out bool buildCommitments)) return "checkpoint mode is missing or invalid (legacy checkpoints require a one-time restart)";
        if (buildCommitments != (_retrofit is not null)) return "checkpoint mode differs from the requested mode";
        if (_retrofit is null) return null;
        if (pendingFrom % _retrofit.WindowGranularity != 0) return "checkpoint start is not aligned for proof building";
        return pendingFrom > from && !CoverageIncludes(from, pendingFrom - 1) ? "checkpoint leaves an unbuilt proof prefix" : null;
    }

    /// <summary>Finds a completed contiguous prefix of the requested range.</summary>
    /// <remarks>A verified prefix is required even when tip capture has published coverage from genesis:
    /// tip capture alone does not replace the first walk and its epoch snapshots. In build mode, published coverage
    /// must contain the requested start; its frontier may trail verification or extend past it with contiguous
    /// tip commitments. Verification alone cannot stand in for unbuilt proofs.</remarks>
    private bool TryGetCompletedPrefix(ulong from, out ulong completedTo)
    {
        if (!_metadata.TryGetWalkVerified(out ulong verifiedFrom, out completedTo)
            || verifiedFrom > from || completedTo < from) return false;
        if (_retrofit is null) return true;
        if (!_metadata.TryGetCoverage(out ulong coveredFrom, out ulong coveredTo)
            || coveredFrom > from || coveredTo < from) return false;

        completedTo = coveredTo;
        return true;
    }

    private bool TipCovers(ulong fromInclusive, ulong toInclusive) =>
        _metadata.TryGetTipSeries(out ulong start, out ulong frontier) && start <= fromInclusive && frontier >= toInclusive;

    private bool CoverageIncludes(ulong fromInclusive, ulong toInclusive) =>
        _metadata.TryGetCoverage(out ulong start, out ulong frontier) && start <= fromInclusive && frontier >= toInclusive;

    private bool VerifiedReaches(ulong fromInclusive) =>
        fromInclusive > 0 && _metadata.TryGetWalkVerified(out ulong _, out ulong verified) && verified >= fromInclusive - 1;

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
