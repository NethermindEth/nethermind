// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
/// has the same complete history the source does and the loop stops. A verification pass
/// (<see cref="ArchiveCloneImporter.VerifyAndBan"/>) is deliberately not wired in here - it needs a sampling
/// policy this runner has no opinion on, and stays available as an explicit, separately-callable step instead of
/// being forced into the automatic kickoff path.
/// </summary>
public sealed class ArchiveCloneCoordinator : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly IFlatDbConfig _config;
    private readonly NHistPeerSelector _selector;
    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly IDb _codeDb;
    private readonly IDb _metadataDb;
    private readonly HistoryWindowPruner _pruner;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
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
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_selector.TryGetEligibleCloneSource(_rowFormat.FormatVersion, NHistPeerSelector.NoExclusions, out PeerInfo peer, out INHistSyncPeer syncPeer))
                {
                    NHistArchiveCloneSource source = NHistArchiveCloneSource.FromPeer(peer, syncPeer);
                    if (_logger.IsInfo) _logger.Info($"Full archive clone starting from peer {peer} (row format {source.RowFormatVersion}, source watermark {source.Watermark}).");
                    ArchiveCloneImporter importer = new(
                        source, _history, _codeDb, _metadataDb, _config, _pruner, _availability, _rowFormat, _logManager);
                    await importer.CloneAsync(token);
                    if (_logger.IsInfo) _logger.Info($"Full archive clone from peer {peer} completed at watermark {source.Watermark}.");
                    return;
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
