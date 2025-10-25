// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.EraE;

public class EraExporter(
    IFileSystem fileSystem,
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
    ISpecProvider specProvider,
    IEraConfig eraConfig,
    ILogManager logManager)
    : Era1.EraExporter(
        fileSystem, 
        blockTree, 
        receiptStorage, 
        specProvider, 
        new Era1.EraConfig { 
            MaxEra1Size = eraConfig.MaxEraESize, 
            NetworkName = eraConfig.NetworkName, 
            Concurrency = eraConfig.Concurrency, 
            ImportBlocksBufferSize = eraConfig.ImportBlocksBufferSize, 
            TrustedAccumulatorFile = eraConfig.TrustedAccumulatorFile, 
            From = eraConfig.From, 
            To = eraConfig.To, 
            ExportDirectory = eraConfig.ExportDirectory, 
            ImportDirectory= eraConfig.ImportDirectory
        }, 
        logManager
    )
{
    protected override EraWriter GetWriter(string filePath, ISpecProvider specProvider)
    {
        return new EraWriter(filePath, specProvider);
    }
}
