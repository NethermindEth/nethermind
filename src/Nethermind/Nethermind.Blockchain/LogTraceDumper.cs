// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain;

public static class BlockTraceDumper
{
    public static string GetDiagnosticFilePath(string name) =>
        Path.Combine(Path.GetTempPath(), name);

    public static string GetInvalidBlockRlpFileName(Hash256 blockHash) =>
        $"block_{blockHash}.rlp";

    public static string GetReceiptsTraceFileName(Hash256 blockHash, bool isSuccess) =>
        $"receipts_{blockHash}_{GetTraceState(isSuccess)}.txt";

    public static string GetParityTraceFileName(Hash256 blockHash, bool isSuccess) =>
        $"parityStyle_{blockHash}_{GetTraceState(isSuccess)}.txt";

    public static string GetGethTraceFileName(Hash256 blockHash, bool isSuccess) =>
        $"gethStyle_{blockHash}_{GetTraceState(isSuccess)}.txt";

    public static IEnumerable<string> GetInvalidBlockDiagnosticPaths(Hash256 blockHash)
    {
        yield return GetDiagnosticFilePath(GetInvalidBlockRlpFileName(blockHash));
        yield return GetDiagnosticFilePath(GetReceiptsTraceFileName(blockHash, isSuccess: false));
        yield return GetDiagnosticFilePath(GetReceiptsTraceFileName(blockHash, isSuccess: true));
        yield return GetDiagnosticFilePath(GetParityTraceFileName(blockHash, isSuccess: false));
        yield return GetDiagnosticFilePath(GetParityTraceFileName(blockHash, isSuccess: true));
        yield return GetDiagnosticFilePath(GetGethTraceFileName(blockHash, isSuccess: false));
        yield return GetDiagnosticFilePath(GetGethTraceFileName(blockHash, isSuccess: true));
    }

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
                string fileName = GetInvalidBlockRlpFileName(blockHash);
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
        Either<Hash256, IList<Block>> blocksOrHash,
        ILogger logger)
    {
        string fileName = string.Empty;
        bool isSuccess = GetConditionAndHashString(blocksOrHash, out string logCondition, out string blockHash);

        string state = isSuccess ? "success" : "failed";
        try
        {
            if (blockTracer is BlockReceiptsTracer receiptsTracer)
            {
                fileName = GetReceiptsTraceFileName(new Hash256(blockHash), isSuccess);
                using FileStream diagnosticFile = GetFileStream(fileName);
                TxReceipt[] receipts = receiptsTracer.TxReceipts.ToArray();
                EthereumJsonSerializer.SerializeToStream(diagnosticFile, receipts, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Receipts trace of {logCondition} block {blockHash} in file {diagnosticFile.Name}");
            }

            if (blockTracer is GethLikeBlockMemoryTracer gethTracer)
            {
                fileName = GetGethTraceFileName(new Hash256(blockHash), isSuccess);
                using FileStream diagnosticFile = GetFileStream(fileName);
                IReadOnlyCollection<GethLikeTxTrace> trace = gethTracer.BuildResult();
                EthereumJsonSerializer.SerializeToStream(diagnosticFile, trace, true);
                if (logger.IsInfo)
                    logger.Info($"Created a Geth-style trace of {logCondition} block {blockHash} in file {diagnosticFile.Name}");
            }

            if (blockTracer is ParityLikeBlockTracer parityTracer)
            {
                fileName = GetParityTraceFileName(new Hash256(blockHash), isSuccess);
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

    private static bool GetConditionAndHashString(Either<Hash256, IList<Block>> blocksOrHash, out string condition, out string blockHash)
    {
        if (blocksOrHash.Is(out Hash256 failedBlockHash))
        {
            condition = "invalid";
            blockHash = failedBlockHash.ToString();
            return false;
        }
        else
        {
            blocksOrHash.To(out IList<Block> blocks);
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
            GetDiagnosticFilePath(name),
            FileMode.Create,
            FileAccess.Write);

    private static string GetTraceState(bool isSuccess) => isSuccess ? "success" : "failed";

    public static void LogTraceFailure(IBlockTracer blockTracer, BlockHeader? parent, Exception exception, ILogger logger)
    {
        if (logger.IsError)
            logger.Error($"Cannot create trace of blocks starting from {parent?.Hash} of type {blockTracer.GetType().Name}", exception);
    }
}
