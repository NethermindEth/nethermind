// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

// todo: maybe split into smaller classes
public interface IBlockAccessListManager
{
    BlockAccessList GeneratedBlockAccessList { get; set; }
    bool Enabled { get; }
    bool ParallelExecutionEnabled { get; }

    void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options);
    void Setup(Block block);
    void SpendGas(long gas);
    void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);
    ITransactionProcessorAdapter GetTxProcessor(int? balIndex = null);
    void NextTransaction();
    void Rollback();
    void ReturnTxProcessor(int balIndex);

    /// <summary>
    /// Acquires a tx processor for <paramref name="balIndex"/> and returns a stack-only lease
    /// whose <c>Dispose</c> calls <see cref="ReturnTxProcessor"/>. Use with a <c>using</c>
    /// block so the pool slot is recycled (and the worker's BAL captured into <c>_perTxBal</c>)
    /// even on exception, without an explicit try/finally.
    /// </summary>
    TxProcessorLease RentTxProcessor(int balIndex)
        => new(GetTxProcessor(balIndex), this, balIndex);

    void IncrementalValidation(Block block, TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, InvalidBlockException? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token);
    void SetBlockAccessList(Block block);
    void ValidateBlockAccessList(Block block, ushort index, bool validateStorageReads = true);
    void StoreBeaconRoot(Block block, IReleaseSpec spec);
    void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec);
    void ProcessWithdrawals(Block block, IReleaseSpec spec);
    void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec);
    void ApplyAuRaPreprocessingChanges(IReleaseSpec spec, Address withdrawalContractAddress);
}

/// <summary>
/// Stack-only handle representing a borrowed tx processor from the pool. Dispose returns
/// the slot via <see cref="IBlockAccessListManager.ReturnTxProcessor"/>. <c>readonly ref
/// struct</c> so it never heap-allocates and can't escape the worker's stack frame.
/// </summary>
public readonly ref struct TxProcessorLease
{
    public readonly ITransactionProcessorAdapter Adapter;
    private readonly IBlockAccessListManager _manager;
    private readonly int _balIndex;

    internal TxProcessorLease(ITransactionProcessorAdapter adapter, IBlockAccessListManager manager, int balIndex)
    {
        Adapter = adapter;
        _manager = manager;
        _balIndex = balIndex;
    }

    public void Dispose() => _manager.ReturnTxProcessor(_balIndex);
}
