// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init.Steps;

/// <summary>
/// Diagnostic step for <see cref="IFlatDbConfig.ScanStoragePrefixes"/>: reports the addresses that share a 4-byte
/// storage key prefix, then exits instead of syncing.
/// </summary>
/// <remarks>
/// Declaring <see cref="InitializeBlockchain"/> as a dependent keeps block processing, network and sync from starting,
/// so the column stays unwritten for the duration of the scan.
/// </remarks>
[RunnerStepDependencies(dependencies: [], dependents: [typeof(InitializeBlockchain)])]
public class ScanFlatStoragePrefixes(
    IColumnsDb<FlatDbColumns> flatDb,
    IFlatDbConfig flatDbConfig,
    IProcessExitSource exitSource,
    ILogManager logManager
) : IStep
{
    private const int TopCount = 50;

    private readonly ILogger _logger = logManager.GetClassLogger<ScanFlatStoragePrefixes>();

    public bool MustInitialize => false;

    public Task Execute(CancellationToken cancellationToken)
    {
        ISortedKeyValueStore storage = (ISortedKeyValueStore)flatDb.GetColumnDb(FlatDbColumns.Storage);
        FlatStoragePrefixScanner.Report report = FlatStoragePrefixScanner.Scan(storage, TopCount, _logger, cancellationToken);

        if (_logger.IsInfo) _logger.Info($"Flat storage 4-byte prefix scan of a {flatDbConfig.Layout} layout.{Environment.NewLine}{report}");

        exitSource.Exit(ExitCodes.Ok);
        return Task.CompletedTask;
    }
}
