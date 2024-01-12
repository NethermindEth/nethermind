// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public interface IDebugBridge
{
    GethLikeTxTrace GetTransactionTrace(Hash256 transactionHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    GethLikeTxTrace GetTransactionTrace(long blockNumber, int index, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    GethLikeTxTrace GetTransactionTrace(Hash256 blockHash, int index, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    GethLikeTxTrace GetTransactionTrace(Rlp blockRlp, Hash256 transactionHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    GethLikeTxTrace? GetTransactionTrace(Transaction transaction, BlockParameter blockParameter, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    IReadOnlyCollection<GethLikeTxTrace> GetBlockTrace(BlockParameter blockParameter, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null);
    IReadOnlyCollection<GethLikeTxTrace> GetBlockTrace(Rlp blockRlp, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    byte[] GetBlockRlp(BlockParameter param);
    Block? GetBlock(BlockParameter param);
    byte[] GetBlockRlp(Hash256 blockHash);
    byte[] GetBlockRlp(long number);
    byte[] GetDbValue(string dbName, byte[] key);
    object GetConfigValue(string category, string name);
    ChainLevelInfo GetLevelInfo(long number);
    int DeleteChainSlice(long startNumber, bool force = false);
    void UpdateHeadBlock(Hash256 blockHash);
    Task<bool> MigrateReceipts(long blockNumber);
    void InsertReceipts(BlockParameter blockParameter, TxReceipt[] receipts);
    SyncReportSymmary GetCurrentSyncStage();
    IEnumerable<string> TraceBlockToFile(Hash256 blockHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    public IEnumerable<Block> GetBadBlocks();
    TxReceipt[]? GetReceiptsForBlock(BlockParameter param);
    Transaction? GetTransactionFromHash(Hash256 hash);
}
