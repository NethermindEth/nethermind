// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        object blocksOrHash,
        ILogger logger)
    {
        string fileName = string.Empty;
        bool isSuccess = GetConditionAndHashString(blocksOrHash, out string logCondition, out string blockHash);

        string state = isSuccess ? "success" : "failed";
        try
        {
            if (blockTracer is BlockReceiptsTracer receiptsTracer)
            {
                fileName = $"receipts_{blockHash}_{state}.txt";
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyList<TxReceipt> receipts = receiptsTracer.TxReceipts;
                EthereumJsonSerializer.SerializeToStream(diagnosticFile, receipts, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Receipts trace of {logCondition} block {blockHash} in file {diagnosticFile.Name}");
            }

            if (blockTracer is GethLikeBlockMemoryTracer gethTracer)
            {
                fileName = $"gethStyle_{blockHash}_{state}.txt";
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyCollection<GethLikeTxTrace> trace = gethTracer.BuildResult();
                EthereumJsonSerializer.SerializeToStream(diagnosticFile, trace, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Geth-style trace of {logCondition} block {blockHash} in file {diagnosticFile.Name}");
            }

            if (blockTracer is ParityLikeBlockTracer parityTracer)
            {
                fileName = $"parityStyle_{blockHash}_{state}.txt";
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyCollection<ParityLikeTxTrace> trace = parityTracer.BuildResult();
                EthereumJsonSerializer.SerializeToStream(diagnosticFile, trace, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Parity-style trace of {logCondition} block {blockHash} in file {diagnosticFile.Name}");
            }
        }
        catch (IOException e)
        {
            if (logger.IsError)
                logger.Error($"Cannot save trace of {logCondition} block {blockHash} in file {fileName}", e);
        }
    }

    private static bool GetConditionAndHashString(object blocksOrHash, out string condition, out string blockHash)
    {
        if (blocksOrHash is Hash256 failedBlockHash)
        {
            condition = "invalid";
            blockHash = failedBlockHash.ToString();
            return false;
        }
        else
        {
            List<Block> blocks = blocksOrHash as List<Block>;
            condition = "valid on rerun";

            if (blocks.Count == 1)
            {
                blockHash = blocks[0].Hash.ToString();
            }
            else
            {
                blockHash = string.Join("|", blocks.Select(b => b.Hash.ToString()));
            }
            return true;
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
