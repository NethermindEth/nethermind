// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.EraE;

public class EraCliRunner
(
    IEraEConfig eraConfig,
    IEraImporter eraImporter,
    Era1.IEraExporter eraExporter,
    ILogManager logManager
): Era1.EraCliRunner(
    new Era1.EraConfig {
        MaxEra1Size = eraConfig.MaxEraESize,
        NetworkName = eraConfig.NetworkName,
        Concurrency = eraConfig.Concurrency,
        ImportBlocksBufferSize = eraConfig.ImportBlocksBufferSize,
        TrustedAccumulatorFile = eraConfig.TrustedAccumulatorFile,
        From = eraConfig.From,
        To = eraConfig.To,
    },
    eraImporter,
    eraExporter,
    logManager
)
{
    protected override async Task Import(CancellationToken cancellation)
    {
        try
        {
            await eraImporter.Import(
                eraConfig.ImportDirectory!,
                eraConfig.From,
                eraConfig.To,
                eraConfig.TrustedAccumulatorFile,
                eraConfig.TrustedHistoricalRootsFile,
                cancellation);
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Warn($"A running import job was cancelled.");
        }
        catch (Exception e) when (e is Era1.EraException or Era1.EraImportException)
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
