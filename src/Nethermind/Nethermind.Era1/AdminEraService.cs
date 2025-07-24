// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Era1;
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
        catch (Exception)
        {
            Interlocked.Exchange(ref _canEnterExport, 1);
            throw;
        }

        return "Started export task";
    }

    public string ImportHistory(string source, long from, long to, string? accumulatorFile)
    {
        if (Interlocked.Exchange(ref _canEnterImport, 0) != 1)
            throw new InvalidOperationException("An import job is already running.");

        try
        {
            _ = StartImportTask(source, accumulatorFile, from, to);
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _canEnterImport, 1);
            throw;
        }

        return "Started import task";

    }

    private async Task StartExportTask(string destination, long from, long to)
    {
        // Creating the task is outside the try block so that argument exception can be cought
        Task task = _eraExporter.Export(
            destination,
            from,
            to,
            cancellation: _processExit.Token);

        try
        {
            await task;
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Error(
                $"A running export job was cancelled. Exported archives in '{destination}' might be in a corrupted state.");
        }
        catch (EraException e)
        {
            _logger.Error("Export error", e);
        }
        catch (Exception e)
        {
            _logger.Error("Export error", e);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _canEnterExport, 1);
        }
    }

    private async Task StartImportTask(string source, string? accumulatorFile, long from, long to)
    {
        Task task = _eraImporter.Import(
            source,
            from,
            to,
            accumulatorFile,
            _processExit.Token);

        try
        {
            await task;
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Error(
                $"A running import job was cancelled. Imported archives from '{source}' might be in an incomplete state.");
        }
        catch (EraException e)
        {
            _logger.Error("Import error", e);
        }
        catch (Exception e)
        {
            _logger.Error("Import error", e);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _canEnterImport, 1);
        }
    }
}
