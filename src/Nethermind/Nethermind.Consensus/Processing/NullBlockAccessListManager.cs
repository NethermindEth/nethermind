// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

public class NullBlockAccessListManager : IBlockAccessListManager
{
    public static NullBlockAccessListManager Instance { get; } = new();

    private NullBlockAccessListManager() { }

    public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
    public bool Enabled => false;
    public bool ParallelExecutionEnabled => false;

    public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options) { }
    public void Setup(Block block) { }
    public void SpendGas(long gas) { }
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) { }
    public ITransactionProcessorAdapter GetTxProcessor(int? balIndex = null) => throw new InvalidOperationException("NullBlockAccessListManager does not provide transaction processors.");
    public void NextTransaction() { }
    public void Rollback() { }
    public void ReturnTxProcessor(int balIndex) { }
    public void IncrementalValidation(Block block, TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, InvalidBlockException? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token) { }
    public void SetBlockAccessList(Block block) { }
    public void ValidateBlockAccessList(Block block, ushort index, bool validateStorageReads = true) { }
    public void StoreBeaconRoot(Block block, IReleaseSpec spec) { }
    public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec) { }
    public void ProcessWithdrawals(Block block, IReleaseSpec spec) { }
    public void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec) { }
    public void ApplyAuRaPreprocessingChanges(IReleaseSpec spec, Address withdrawalContractAddress) { }
}
