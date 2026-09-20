// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History.Changesets;

public sealed class BulkFillSessionFactory(
    IDbFactory factory,
    [KeyFilter(DbNames.Code)] IDb code,
    IColumnsDb<FlatDbColumns> flat,
    IColumnsDb<FlatHistoryColumns> history,
    HistoryAvailability availability,
    HistoryRowFormat format,
    HistoryScopeGate scopes,
    IFlatDbConfig config,
    ILogManager logs)
{
    private readonly ILogger _logger = logs.GetClassLogger<BulkFillSessionFactory>();
    private long _lastDiskCheck;
    private BulkFillSession? _lastCheckedSession;

    public BulkFillSession Open(Hash256 genesis, BlockHeader anchor)
    {
        if (format.IsV3 || config.Layout != FlatLayout.Flat || config.HistoryTransactionIndexBulkFillMaxGiB <= 0)
            throw new NotSupportedException("Bulk transaction indexing requires v2 flat history and a positive scratch limit.");
        ValidateSource(anchor);
        bool rlpWrapped = BasePersistence.ResolveSlotEncoding(flat, (ISortedKeyValueStore)flat.GetColumnDb(FlatDbColumns.Storage), _logger);
        Span<byte> identity = stackalloc byte[3 * Hash256.Size + sizeof(ulong) + 2];
        genesis.Bytes.CopyTo(identity);
        (anchor.Hash ?? throw new InvalidDataException("Missing bulk anchor hash.")).Bytes.CopyTo(identity[Hash256.Size..]);
        anchor.StateRoot!.Bytes.CopyTo(identity[(2 * Hash256.Size)..]);
        BinaryPrimitives.WriteUInt64BigEndian(identity[(3 * Hash256.Size)..], anchor.Number);
        identity[^2] = format.FormatVersion;
        identity[^1] = rlpWrapped ? (byte)1 : (byte)0;
        return new BulkFillSession(factory, code, Keccak.Compute(identity), anchor, rlpWrapped);
    }

    public void Import(BulkFillSession session, BlockHeader anchor, Action validateCanonical, CancellationToken token)
    {
        if (session.IsReady) return;
        int stage = 0;
        foreach (FlatHistoryColumns column in new[] { FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears })
        {
            stage++;
            if (_logger.IsInfo) _logger.Info($"Bulk transaction index importing {column} at block {anchor.Number} into {session.DirectoryPath}.");
            bool complete;
            long importStartedAt = Stopwatch.GetTimestamp();
            long reportedAt = importStartedAt;
            long scanned = 0;
            double initialFraction = session.ImportFraction(column);
            do
            {
                long startedAt = Stopwatch.GetTimestamp();
                token.ThrowIfCancellationRequested();
                CheckDisk(session);
                long lease = scopes.EnterScope();
                HistoricalStateScan.Page page;
                try
                {
                    ValidateSource(anchor);
                    validateCanonical();
                    page = session.ReadImportPage((ISortedKeyValueStore)history.GetColumnDb(column), format, column, token);
                }
                finally
                {
                    scopes.ExitScope(lease);
                }
                complete = page.Complete;
                scanned += page.Scanned;
                if (complete || Stopwatch.GetElapsedTime(reportedAt) >= TimeSpan.FromSeconds(30))
                {
                    double fraction = complete ? 1 : page.Position is { } position ? BulkFillImportProgress.Fraction(position.Key) : 0;
                    if (_logger.IsInfo) _logger.Info(BulkFillImportProgress.Format(column, stage, scanned, initialFraction,
                        fraction, Stopwatch.GetElapsedTime(importStartedAt), complete, session.Size));
                    reportedAt = Stopwatch.GetTimestamp();
                }
                Rest(Stopwatch.GetElapsedTime(startedAt), token);
            } while (!complete);
        }
        validateCanonical();
        ValidateSource(anchor);
        if (_logger.IsInfo) _logger.Info($"Bulk transaction index verifying scratch root at block {anchor.Number}.");
        session.VerifyAnchor(token);
        validateCanonical();
        ValidateSource(anchor);
        session.CleanStorage(token);
    }

    public void ValidateSource(BlockHeader header)
    {
        if (format.IsV3 || header.StateRoot is null || !availability.Matches(header.Number, header.StateRoot.ValueHash256))
            throw new InvalidDataException($"Bulk replay source state is unavailable at {header.Number}.");
    }

    public void CheckDisk(BulkFillSession session)
    {
        if (ReferenceEquals(session, _lastCheckedSession) && Stopwatch.GetElapsedTime(_lastDiskCheck) < TimeSpan.FromSeconds(1)) return;
        if (session.Size >= (long)config.HistoryTransactionIndexBulkFillMaxGiB * 1024 * 1024 * 1024)
            throw new IOException("Bulk transaction index reached its scratch disk limit; checkpoint retained.");
        string fullPath = Path.GetFullPath(session.DirectoryPath);
        DriveInfo? selected = null;
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string root = Path.TrimEndingDirectorySeparator(drive.Name) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(root, StringComparison.Ordinal) && (selected is null || drive.Name.Length > selected.Name.Length)) selected = drive;
        }
        if (selected is not null && selected.AvailableFreeSpace < 20L * 1024 * 1024 * 1024)
            throw new IOException("Bulk transaction index requires at least 20 GiB free; checkpoint retained.");
        _lastCheckedSession = session;
        _lastDiskCheck = Stopwatch.GetTimestamp();
    }

    public void Rest(TimeSpan worked, CancellationToken token)
    {
        int duty = Math.Clamp(config.HistoryTransactionIndexDutyCyclePercent, 1, 100);
        TimeSpan rest = worked * (100 - duty) / duty;
        while (rest > TimeSpan.Zero)
        {
            TimeSpan part = rest > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : rest;
            if (token.WaitHandle.WaitOne(part)) token.ThrowIfCancellationRequested();
            rest -= part;
        }
    }
}
