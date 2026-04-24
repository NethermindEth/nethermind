// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.EraE.Admin;

public sealed class AdminEraService(
    IEraImporter eraImporter,
    IEraExporter eraExporter,
    IProcessExitSource processExit,
    ILogManager logManager) : IAdminEraService
{
    private readonly ILogger _logger = logManager.GetClassLogger<AdminEraService>();
    private int _canEnterImport = 1;
    private int _canEnterExport = 1;

    public ResultWrapper<string> ExportHistory(string destination, long from, long to)
    {
        if (Interlocked.Exchange(ref _canEnterExport, 0) != 1)
            return ResultWrapper<string>.Fail("An export job is already running.");

        _ = EraJobRunner.RunProtected(
            ct => eraExporter.Export(destination, from, to, ct),
            $"EraE export was cancelled. Archives in '{destination}' may be incomplete.",
            "EraE export error",
            _logger,
            processExit.Token,
            () => Interlocked.Exchange(ref _canEnterExport, 1));

        return ResultWrapper<string>.Success("Started EraE export task.");
    }

    public ResultWrapper<string> ImportHistory(string source, long from, long to, string? accumulatorFile)
    {
        if (Interlocked.Exchange(ref _canEnterImport, 0) != 1)
            return ResultWrapper<string>.Fail("An import job is already running.");

        _ = EraJobRunner.RunProtected(
            ct => eraImporter.Import(source, from, to, accumulatorFile, ct),
            $"EraE import was cancelled. State from '{source}' may be incomplete.",
            "EraE import error",
            _logger,
            processExit.Token,
            () => Interlocked.Exchange(ref _canEnterImport, 1));

        return ResultWrapper<string>.Success("Started EraE import task.");
    }
}
