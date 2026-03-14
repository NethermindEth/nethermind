// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EraE.Config;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;

namespace Nethermind.EraE;

public class EraCliRunner(
    IEraEConfig eraConfig,
    IEraImporter eraImporter,
    IEraExporter eraExporter)
{
    public async Task Run(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(eraConfig.ImportDirectory))
            await eraImporter.Import(eraConfig.ImportDirectory!, eraConfig.From, eraConfig.To, eraConfig.TrustedAccumulatorFile, token);
        else if (!string.IsNullOrEmpty(eraConfig.ExportDirectory))
            await eraExporter.Export(eraConfig.ExportDirectory!, eraConfig.From, eraConfig.To, token);
    }
}
