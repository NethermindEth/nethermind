// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.Era1;

public class EraCliRunner(
    IEraConfig eraConfig,
    IEraImporter eraImporter,
    IEraExporter eraExporter,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<EraCliRunner>();

    public async Task Run(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(eraConfig.ImportDirectory))
        {
            await Import(token);
        }
        else if (!string.IsNullOrEmpty(eraConfig.ExportDirectory))
        {
            await Export(token);
        }
    }

    private async Task Export(CancellationToken cancellation)
    {
        try
        {
            await eraExporter.Export(eraConfig.ExportDirectory!, eraConfig.From, eraConfig.To, cancellation: cancellation);
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Warn($"A running export job was cancelled.");
        }
        catch (Exception e) when (e is EraException)
        {
            _logger.Error($"The export failed with the message: {e.Message}");
        }
        catch (Exception e)
        {
            _logger.Error("Export error", e);
            throw;
        }
    }

    private async Task Import(CancellationToken cancellation)
    {
        try
        {
            await eraImporter.Import(eraConfig.ImportDirectory!, eraConfig.From, eraConfig.To, eraConfig.TrustedAccumulatorFile, cancellation);
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Warn($"A running import job was cancelled.");
        }
        catch (Exception e) when (e is EraException or EraImportException)
        {
            _logger.Error($"The import failed with the message: {e.Message}");
        }
        catch (Exception e)
        {
            _logger.Error("Import error", e);
            throw;
        }
    }
}
