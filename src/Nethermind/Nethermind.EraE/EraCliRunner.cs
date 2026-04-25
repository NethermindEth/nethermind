// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EraE.Config;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using Nethermind.History;
using EraException = Nethermind.Era1.EraException;

namespace Nethermind.EraE;

public sealed class EraCliRunner(
    IEraEConfig eraConfig,
    IHistoryConfig historyConfig,
    IEraImporter eraImporter,
    IEraExporter eraExporter)
{
    public async Task Run(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(eraConfig.ImportDirectory))
        {
            if (historyConfig.Pruning == PruningModes.UseAncientBarriers)
            {
                throw new EraException(
                    "EraE import is configured alongside History.Pruning=UseAncientBarriers. " +
                    "This would immediately prune the imported blocks. " +
                    "Either disable history pruning or remove the import directory.");
            }

            await eraImporter.Import(eraConfig.ImportDirectory!, eraConfig.From, eraConfig.To, eraConfig.TrustedAccumulatorFile, token);
        }
        else if (!string.IsNullOrEmpty(eraConfig.ExportDirectory))
        {
            await eraExporter.Export(eraConfig.ExportDirectory!, eraConfig.From, eraConfig.To, token);
        }
    }
}
