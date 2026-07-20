// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

public interface IBlockAccessListManager
{
    GeneratedBlockAccessList GeneratedBlockAccessList { get; set; }
    bool Enabled { get; }
    bool ParallelExecutionEnabled { get; }
    bool BatchReadEnabled { get; }

    /// <summary>When set, the manager always builds the constructed GeneratedBlockAccessList
    /// even on the parallel-validation path. BAL recorder must set this before
    /// PrepareForProcessing.</summary>
    bool ForceConstructGeneratedBlockAccessList { get; set; }

    void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options);

    /// <summary>
    /// Blocks until the BAL read-warming task started by <see cref="PrepareForProcessing"/>
    /// (if any) completes, then forgets it.
    /// </summary>
    /// <remarks>
    /// Warming is best-effort: cancellation is expected and faults must never fail the block
    /// — they only mean fewer pre-block cache hits.
    /// </remarks>
    void WaitForBalWarmup();

    void Setup(Block block);
    void SpendGas(ulong gas);
    void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);
    ITransactionProcessorAdapter GetTxProcessor(uint? balIndex = null);
    void NextTransaction();
    void Rollback();
    void ReturnTxProcessor(uint balIndex);

    /// <summary>
    /// Acquires a tx processor for <paramref name="balIndex"/> and returns a stack-only lease
    /// whose <c>Dispose</c> calls <see cref="ReturnTxProcessor"/>. Use with a <c>using</c>
    /// block so the pool slot is recycled (and the worker's BAL captured into <c>_perTxBal</c>)
    /// even on exception, without an explicit try/finally.
    /// </summary>
    TxProcessorLease RentTxProcessor(uint balIndex)
        => new(GetTxProcessor(balIndex), this, balIndex);

    void IncrementalValidation(Block block, GasValidationResultSlot[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token);
    void SetBlockAccessList(Block block);
    void ValidateBlockAccessList(Block block, uint index, bool validateStorageReads = true);
    void StoreBeaconRoot(Block block, IReleaseSpec spec);
    void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec);
    void InstallExpiryVerifierCode(IReleaseSpec spec);
    void ProcessWithdrawals(Block block, IReleaseSpec spec);
    void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec);
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
    private readonly uint _balIndex;

    internal TxProcessorLease(ITransactionProcessorAdapter adapter, IBlockAccessListManager manager, uint balIndex)
    {
        Adapter = adapter;
        _manager = manager;
        _balIndex = balIndex;
    }

    public void Dispose() => _manager.ReturnTxProcessor(_balIndex);
}
