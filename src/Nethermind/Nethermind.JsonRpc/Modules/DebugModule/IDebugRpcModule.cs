// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.JsonRpc.Modules.DebugModule;

[RpcModule(ModuleType.Debug)]
public interface IDebugRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Retrieves a representation of tree branches on a given chain level (Nethermind specific).", IsImplemented = true, IsSharable = true)]
    ResultWrapper<ChainLevelForRpc> debug_getChainLevel(in long number);

    [JsonRpcMethod(Description = "Deletes a slice of a chain from the tree on all branches (Nethermind specific).", IsImplemented = true, IsSharable = true)]
    ResultWrapper<int> debug_deleteChainSlice(in long startNumber, bool force = false);

    [JsonRpcMethod(
        Description = "Updates / resets head block - use only when the node got stuck due to DB / memory corruption (Nethermind specific).",
        IsSharable = true)]
    ResultWrapper<bool> debug_resetHead(Hash256 blockHash);

    [JsonRpcMethod(Description = "This method will attempt to run the transaction in the exact same manner as it was executed on the network. It will replay any transaction that may have been executed prior to this one before it will finally attempt to execute the transaction that corresponds to the given hash.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Hash256 transactionHash, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "This method lets you run an eth_call within the context of the given block execution using the final state of parent block as the base. The block can be specified either by hash or by number. It takes the same input object as a eth_call. It returns the same output as debug_traceTransaction.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<GethLikeTxTrace> debug_traceCall(TransactionForRpc call, BlockParameter? blockParameter = null, GethTraceOptions? options = null);

    [JsonRpcMethod(Description = "", IsSharable = true)]
    ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int txIndex, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "", IsSharable = true)]
    ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Hash256 blockHash, int txIndex, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "Returns the full stack trace of all invoked opcodes of all transactions that were included in the block specified. The parent of the block must be present or it will fail.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "Similar to debug_traceBlock, this method accepts a block number as well as \"latest\" or \"finalized\" and replays the block that is already present in the database.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByNumber(BlockParameter blockParameter, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "Similar to debug_traceBlock, this method accepts a block hash and replays the block that is already present in the database.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByHash(Hash256 blockHash, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = false)]
    ResultWrapper<GethLikeTxTrace[]> debug_traceBlockFromFile(string fileName, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = false)]
    ResultWrapper<object> debug_dumpBlock(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = true)]
    ResultWrapper<GcStats> debug_gcStats();

    [JsonRpcMethod(Description = "Retrieves a block in the RLP-serialized form.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<byte[]> debug_getBlockRlp(long number);

    [JsonRpcMethod(Description = "Retrieves a block in the RLP-serialized form.", IsImplemented = true, IsSharable = false)]
    ResultWrapper<byte[]> debug_getBlockRlpByHash(Hash256 hash);

    [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = true)]
    ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = true)]
    ResultWrapper<byte[]> debug_seedHash(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = false)]
    ResultWrapper<bool> debug_setHead(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = true)]
    ResultWrapper<byte[]> debug_getFromDb(string dbName, byte[] key);

    [JsonRpcMethod(Description = "Retrieves the Nethermind configuration value, e.g. JsonRpc.Enabled", IsImplemented = true, IsSharable = true)]
    ResultWrapper<object> debug_getConfigValue(string category, string name);

    [JsonRpcMethod(Description = "", IsImplemented = true, IsSharable = false)]
    ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Hash256 transactionHash, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "", IsImplemented = true, IsSharable = false)]
    ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "Sets the block number up to which receipts will be migrated to (Nethermind specific).")]
    Task<ResultWrapper<bool>> debug_migrateReceipts(long blockNumber);

    [JsonRpcMethod(Description = "Insert receipts for the block after verifying receipts root correctness.")]
    Task<ResultWrapper<bool>> debug_insertReceipts(BlockParameter blockParameter, ReceiptForRpc[] receiptForRpc);

    [JsonRpcMethod(Description = "Get Raw Block format.")]
    ResultWrapper<byte[]> debug_getRawBlock(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "Get Raw Receipt format.")]
    ResultWrapper<byte[][]> debug_getRawReceipts(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "Get Raw Header format.")]
    ResultWrapper<byte[]> debug_getRawHeader(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "Get Raw Transaction format.")]
    ResultWrapper<byte[]> debug_getRawTransaction(Hash256 transactionHash);

    [JsonRpcMethod(Description = "Retrives Nethermind Sync Stage, With extra Metadata")]
    Task<ResultWrapper<SyncReportSymmary>> debug_getSyncStage();

    [JsonRpcMethod(Description = "Writes to a file the full stack trace of all invoked opcodes of the transaction specified (or all transactions if not specified) that was included in the block specified. The parent of the block must be present or it will fail.",
        IsImplemented = true, IsSharable = false)]
    ResultWrapper<IEnumerable<string>> debug_standardTraceBlockToFile(Hash256 blockHash, GethTraceOptions options = null);

    [JsonRpcMethod(Description = "Return list of invalid blocks.")]
    ResultWrapper<IEnumerable<BadBlock>> debug_getBadBlocks();
}
