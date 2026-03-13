// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using EraException = Nethermind.Era1.EraException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using Nethermind.Logging;

namespace Nethermind.EraE.Admin;

public class AdminEraService : IAdminEraService
{
    private readonly ILogger _logger;
    private readonly IEraImporter _eraImporter;
    private readonly IEraExporter _eraExporter;
    private readonly IProcessExitSource _processExit;
    private int _canEnterImport = 1;
    private int _canEnterExport = 1;

    public AdminEraService(
        IEraImporter eraImporter,
        IEraExporter eraExporter,
        IProcessExitSource processExit,
        ILogManager logManager)
    {
        _eraImporter = eraImporter;
        _eraExporter = eraExporter;
        _processExit = processExit;
        _logger = logManager.GetClassLogger();
    }

    public string ExportHistory(string destination, long from, long to)
    {
        if (Interlocked.Exchange(ref _canEnterExport, 0) != 1)
            throw new InvalidOperationException("An export job is already running.");

        try
        {
            _ = StartExportTask(destination, from, to);
        }
        catch
        {
            Interlocked.Exchange(ref _canEnterExport, 1);
            throw;
        }

        return "Started EraE export task.";
    }

    public string ImportHistory(string source, long from, long to, string? accumulatorFile)
    {
        if (Interlocked.Exchange(ref _canEnterImport, 0) != 1)
            throw new InvalidOperationException("An import job is already running.");

        try
        {
            _ = StartImportTask(source, accumulatorFile, from, to);
        }
        catch
        {
            Interlocked.Exchange(ref _canEnterImport, 1);
            throw;
        }

        return "Started EraE import task.";
    }

    private async Task StartExportTask(string destination, long from, long to)
    {
        Task task = _eraExporter.Export(destination, from, to, cancellation: _processExit.Token);
        try
        {
            await task;
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Error($"EraE export was cancelled. Archives in '{destination}' may be incomplete.");
        }
        catch (EraException e)
        {
            _logger.Error("EraE export error", e);
        }
        catch (Exception e)
        {
            _logger.Error("EraE export error", e);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _canEnterExport, 1);
        }
    }

    private async Task StartImportTask(string source, string? accumulatorFile, long from, long to)
    {
        Task task = _eraImporter.Import(source, from, to, accumulatorFile, _processExit.Token);
        try
        {
            await task;
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Error($"EraE import was cancelled. State from '{source}' may be incomplete.");
        }
        catch (EraException e)
        {
            _logger.Error("EraE import error", e);
        }
        catch (Exception e)
        {
            _logger.Error("EraE import error", e);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _canEnterImport, 1);
        }
    }
}
