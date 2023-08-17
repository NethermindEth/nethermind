// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.Blockchain;

public static class BlockTraceDumper
{
    public static List<JsonConverter> Converters { get; } = new List<JsonConverter>();

    public static void LogDiagnosticTrace(
        IBlockTracer blockTracer,
        Keccak blockHash,
        ILogger logger)
    {
        static FileStream GetFileStream(string name) =>
            new(
                Path.Combine(Path.GetTempPath(), name),
                FileMode.Create,
                FileAccess.Write);

        string fileName = string.Empty;

        try
        {
            IJsonSerializer serializer = new EthereumJsonSerializer();
            serializer.RegisterConverters(Converters);

            if (blockTracer is BlockReceiptsTracer receiptsTracer)
            {
                fileName = $"receipts_{blockHash}.txt";
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyList<TxReceipt> receipts = receiptsTracer.TxReceipts;
                serializer.Serialize(diagnosticFile, receipts, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Receipts trace of block {blockHash} in file {diagnosticFile.Name}");

            }

            if (blockTracer is GethLikeBlockMemoryTracer gethTracer)
            {
                fileName = $"gethStyle_{blockHash}.txt";
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyCollection<GethLikeTxTrace> trace = gethTracer.BuildResult();
                serializer.Serialize(diagnosticFile, trace, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Geth-style trace of block {blockHash} in file {diagnosticFile.Name}");
            }

            if (blockTracer is ParityLikeBlockTracer parityTracer)
            {
                fileName = $"parityStyle_{blockHash}.txt";
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyCollection<ParityLikeTxTrace> trace = parityTracer.BuildResult();
                serializer.Serialize(diagnosticFile, trace, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Parity-style trace of block {blockHash} in file {diagnosticFile.Name}");
            }
        }
        catch (IOException e)
        {
            if (logger.IsError)
                logger.Error($"Cannot save trace of block {blockHash} in file {fileName}", e);
        }
    }

    public static void LogTraceFailure(IBlockTracer blockTracer, Keccak blockHash, Exception exception, ILogger logger)
    {
        if (logger.IsError)
            logger.Error($"Cannot create trace of blocks starting from {blockHash} of type {blockTracer.GetType().Name}", exception);
    }
}
