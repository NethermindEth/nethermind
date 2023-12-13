// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain;

public static class BlockTraceDumper
{
    public static void LogDiagnosticRlp(
        Block block,
        ILogger logger,
        bool toFile,
        bool toLog)
    {
        if (toFile || toLog)
        {
            Rlp rlp = new BlockDecoder().Encode(block, RlpBehaviors.AllowExtraBytes);
            Hash256 blockHash = block.Hash;
            if (toFile)
            {
                string fileName = $"block_{blockHash}.rlp";
                using FileStream diagnosticFile = GetFileStream(fileName);
                diagnosticFile.Write(rlp.Bytes);
                if (logger.IsInfo)
                    logger.Info($"Created a RLP dump of invalid block {blockHash} in file {diagnosticFile.Name}");
            }

            if (toLog)
            {
                if (logger.IsInfo) logger.Info($"RLP dump of invalid block {blockHash} is {rlp.Bytes.ToHexString()}");
            }
        }
    }

    public static void LogDiagnosticTrace(
        IBlockTracer blockTracer,
        Hash256 blockHash,
        ILogger logger)
    {
        string fileName = string.Empty;

        try
        {
            if (blockTracer is BlockReceiptsTracer receiptsTracer)
            {
                fileName = $"receipts_{blockHash}.txt";
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyList<TxReceipt> receipts = receiptsTracer.TxReceipts;
                EthereumJsonSerializer.SerializeToStream(diagnosticFile, receipts, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Receipts trace of invalid block {blockHash} in file {diagnosticFile.Name}");
            }

            if (blockTracer is GethLikeBlockMemoryTracer gethTracer)
            {
                fileName = $"gethStyle_{blockHash}.txt";
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyCollection<GethLikeTxTrace> trace = gethTracer.BuildResult();
                EthereumJsonSerializer.SerializeToStream(diagnosticFile, trace, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Geth-style trace of invalid block {blockHash} in file {diagnosticFile.Name}");
            }

            if (blockTracer is ParityLikeBlockTracer parityTracer)
            {
                fileName = $"parityStyle_{blockHash}.txt";
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyCollection<ParityLikeTxTrace> trace = parityTracer.BuildResult();
                EthereumJsonSerializer.SerializeToStream(diagnosticFile, trace, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Parity-style trace of invalid block {blockHash} in file {diagnosticFile.Name}");
            }
        }
        catch (IOException e)
        {
            if (logger.IsError)
                logger.Error($"Cannot save trace of block {blockHash} in file {fileName}", e);
        }
    }

    private static FileStream GetFileStream(string name) =>
        new(
            Path.Combine(Path.GetTempPath(), name),
            FileMode.Create,
            FileAccess.Write);

    public static void LogTraceFailure(IBlockTracer blockTracer, Hash256 blockHash, Exception exception, ILogger logger)
    {
        if (logger.IsError)
            logger.Error($"Cannot create trace of blocks starting from {blockHash} of type {blockTracer.GetType().Name}", exception);
    }
}
