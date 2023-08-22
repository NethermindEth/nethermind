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
    GethLikeTxTrace GetTransactionTrace(Keccak transactionHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    GethLikeTxTrace GetTransactionTrace(long blockNumber, int index, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    GethLikeTxTrace GetTransactionTrace(Keccak blockHash, int index, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    GethLikeTxTrace GetTransactionTrace(Rlp blockRlp, Keccak transactionHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    GethLikeTxTrace? GetTransactionTrace(Transaction transaction, BlockParameter blockParameter, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    GethLikeTxTrace[] GetBlockTrace(BlockParameter blockParameter, CancellationToken cancellationToken, GethTraceOptions gethTraceOptions = null);
    GethLikeTxTrace[] GetBlockTrace(Rlp blockRlp, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
    byte[] GetBlockRlp(Keccak blockHash);
    byte[] GetBlockRlp(long number);
    byte[] GetDbValue(string dbName, byte[] key);
    object GetConfigValue(string category, string name);
    public ChainLevelInfo GetLevelInfo(long number);
    public int DeleteChainSlice(long startNumber);
    public void UpdateHeadBlock(Keccak blockHash);
    Task<bool> MigrateReceipts(long blockNumber);
    void InsertReceipts(BlockParameter blockParameter, TxReceipt[] receipts);
    SyncReportSymmary GetCurrentSyncStage();
    IEnumerable<string> TraceBlockToFile(Keccak blockHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null);
}
