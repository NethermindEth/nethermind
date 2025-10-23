// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.EraE;
public class AdminEraService(
    IEraImporter eraImporter,
    Era1.IEraExporter eraExporter,
    IProcessExitSource processExit,
    ILogManager logManager)
    : Era1.AdminEraService(eraImporter, eraExporter, processExit, logManager), IAdminEraService
{
    public string ImportHistory(string source, long from, long to, string? accumulatorFile, string? historicalRootsFile)
    {
        if (Interlocked.Exchange(ref _canEnterImport, 0) != 1)
            throw new InvalidOperationException("An import job is already running.");

        try
        {
            _ = StartImportTask(source, accumulatorFile, historicalRootsFile, from, to);
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _canEnterImport, 1);
            throw;
        }

        return "Started import task";

    }

    private async Task StartImportTask(string source, string? accumulatorFile, string? historicalRootsFile, long from, long to)
    {
        Task task = ((IEraImporter)_eraImporter).Import(
            source,
            from,
            to,
            accumulatorFile,
            historicalRootsFile,
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
        catch (Era1.EraException e)
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