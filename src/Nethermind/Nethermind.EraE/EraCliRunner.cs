// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.EraE;

public class EraCliRunner(
    IEraConfig eraConfig,
    Era1.IEraImporter eraImporter,
    Era1.IEraExporter eraExporter,
    ILogManager logManager)
    : Era1.EraCliRunner(
        new Era1.EraConfig {
            MaxEra1Size = eraConfig.MaxEraESize,
            NetworkName = eraConfig.NetworkName,
            Concurrency = eraConfig.Concurrency,
            ImportBlocksBufferSize = eraConfig.ImportBlocksBufferSize,
            TrustedAccumulatorFile = eraConfig.TrustedAccumulatorFile,
            From = eraConfig.From,
            To = eraConfig.To,
            ImportDirectory = eraConfig.ImportDirectory
        }, 
        eraImporter, 
        eraExporter, 
        logManager
    );
