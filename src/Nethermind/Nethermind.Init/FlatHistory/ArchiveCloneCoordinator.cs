// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Init.FlatHistory;

/// <summary>
/// Owns the one-shot, node-lifetime full-archive-clone attempt when <c>Flat.HistoryArchiveCloneEnabled</c> is on:
/// once <see cref="ArchiveCloneImporter.CloneAsync"/> completes and publishes the source's watermark, this node
/// has the same complete history the source does and the loop stops. Publishing is gated on the imported state
/// roots agreeing with the headers this node synced independently; a source that fails the gate throws instead of
/// publishing, and this loop then retries against whichever source is eligible next. The deeper, per-height
/// re-derivation (<see cref="ArchiveCloneImporter.VerifyAndBan"/>) stays a separately-callable step - it needs a
/// sampling policy this runner has no opinion on.
/// </summary>
public sealed class ArchiveCloneCoordinator : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DiagnosticsInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CaptureConnectionPollDelay = TimeSpan.FromSeconds(30);

    private readonly IFlatDbConfig _config;
    private readonly NHistPeerSelector _selector;
    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly IDb _codeDb;
    private readonly IDb _metadataDb;
    private readonly HistoryWindowPruner _pruner;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
    private readonly ArchiveCloneVerifier _verifier;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public ArchiveCloneCoordinator(
        IFlatDbConfig config,
        NHistPeerSelector selector,
        IColumnsDb<FlatHistoryColumns> history,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        HistoryWindowPruner pruner,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        ArchiveCloneVerifier verifier,
        ILogManager logManager)
    {
        if (config.HistoryArchiveCloneEnabled && config.HistoryRetentionBlocks > 0)
        {
            throw new InvalidConfigurationException(
                "Flat.HistoryArchiveCloneEnabled clones a FULL archive and cannot be combined with a bounded Flat.HistoryRetentionBlocks window; unset one of the two.", -1);
        }

        _config = config;
        _selector = selector;
        _history = history;
        _codeDb = codeDb;
        _metadataDb = metadataDb;
        _pruner = pruner;
        _availability = availability;
        _rowFormat = rowFormat;
        _verifier = verifier;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<ArchiveCloneCoordinator>();

        Started = config.HistoryArchiveCloneEnabled;
        _loop = Started ? RunLoopAsync() : Task.CompletedTask;
    }

    /// <summary>Whether this instance actually started its background clone attempt - false means
    /// <c>Flat.HistoryArchiveCloneEnabled</c> is off and this coordinator is a permanent no-op. See
    /// <see cref="WindowBackfillCoordinator.Started"/> for why this is public.</summary>
    public bool Started { get; }

    private async Task RunLoopAsync()
    {
        CancellationToken token = _cts.Token;
        long lastDiagnosticsTimestamp = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                Action<string>? diagnostics = null;
                long now = Stopwatch.GetTimestamp();
                if (_logger.IsInfo && Stopwatch.GetElapsedTime(lastDiagnosticsTimestamp, now) > DiagnosticsInterval)
                {
                    lastDiagnosticsTimestamp = now;
                    diagnostics = reason => _logger.Info($"Archive clone waiting for an eligible source: {reason}.");
                }

                // A pass interrupted mid-stream resumes against the target it froze, so a replacement source has to
                // cover at least that height - one that stops below it would leave the top of the range unfetched
                // while the pass went on to publish the frozen target as covered.
                ArchiveCloneImporter.TryReadStoredTargetWatermark(_metadataDb, out ulong resumeWatermark);

                if (_selector.TryGetEligibleCloneSource(_rowFormat.FormatVersion, resumeWatermark, NHistPeerSelector.NoExclusions, out PeerInfo peer, out INHistSyncPeer syncPeer, diagnostics))
                {
                    NHistArchiveCloneSource source = NHistArchiveCloneSource.FromPeer(peer, syncPeer);
                    if (_logger.IsInfo) _logger.Info($"Full archive clone starting from peer {peer} (row format {source.RowFormatVersion}, source watermark {source.Watermark}).");
                    ArchiveCloneImporter importer = new(
                        source, _history, _codeDb, _metadataDb, _config, _pruner, _availability, _rowFormat, _verifier, _logManager);
                    ulong targetWatermark = await importer.CloneAsync(token);
                    if (_logger.IsInfo) _logger.Info($"Full archive clone from peer {peer} completed at watermark {targetWatermark}.");

                    if (await WaitForCaptureConnectionAsync(targetWatermark, token))
                    {
                        return;
                    }

                    // The clone's frozen watermark landed below this node's own capture start (the snap pivot kept
                    // moving while the clone streamed), so the two ranges do not touch. A fresh pass reads a fresh,
                    // strictly newer source watermark; the capture floor is fixed, so the gap closes in one or two
                    // passes and this loop terminates.
                    if (_logger.IsInfo) _logger.Info(
                        $"The cloned history ends at block {targetWatermark}, below this node's own capture start; " +
                        "re-cloning against the source's current watermark to close the gap.");
                    importer.ResetForNewTarget();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Full archive clone attempt failed; retrying in {RetryDelay}. {e}");
            }

            try
            {
                await Task.Delay(RetryDelay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> WaitForCaptureConnectionAsync(ulong targetWatermark, CancellationToken token)
    {
        long lastLogTimestamp = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            if (_availability.TryGetWatermark(out ulong watermark) && watermark > targetWatermark)
            {
                if (_logger.IsInfo) _logger.Info($"Full archive clone finished: this node's capture connected and history is served up to block {watermark}.");
                return true;
            }

            if (_availability.TryGetPendingCaptureRange(out ulong pendingFirst, out _) && pendingFirst > targetWatermark + 1)
            {
                return false;
            }

            if (_logger.IsInfo && Stopwatch.GetElapsedTime(lastLogTimestamp) > DiagnosticsInterval)
            {
                lastLogTimestamp = Stopwatch.GetTimestamp();
                _logger.Info($"Archive clone complete at watermark {targetWatermark}; waiting for this node's own capture to connect.");
            }

            await Task.Delay(CaptureConnectionPollDelay, token);
        }

        return true;
    }

    public void Dispose()
    {
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
