// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EraE.Config;
using Nethermind.EraE.Exceptions;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using Nethermind.Logging;

namespace Nethermind.EraE;

public class EraCliRunner(
    IEraEConfig eraConfig,
    IEraImporter eraImporter,
    IEraExporter eraExporter,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<EraCliRunner>();

    public async Task Run(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(eraConfig.ImportDirectory))
            await Import(token);
        else if (!string.IsNullOrEmpty(eraConfig.ExportDirectory))
            await Export(token);
    }

    private async Task Export(CancellationToken cancellation)
    {
        try
        {
            await eraExporter.Export(eraConfig.ExportDirectory!, eraConfig.From, eraConfig.To, cancellation: cancellation);
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Warn("EraE export was cancelled.");
        }
        catch (EraException e)
        {
            _logger.Error($"EraE export failed: {e.Message}");
        }
        catch (Exception e)
        {
            _logger.Error("EraE export error", e);
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
            _logger.Warn("EraE import was cancelled.");
        }
        catch (Exception e) when (e is EraException or EraImportException)
        {
            _logger.Error($"EraE import failed: {e.Message}");
        }
        catch (Exception e)
        {
            _logger.Error("EraE import error", e);
            throw;
        }
    }
}
