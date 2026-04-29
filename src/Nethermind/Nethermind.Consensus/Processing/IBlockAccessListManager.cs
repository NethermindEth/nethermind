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
    void IncrementalValidation(Block block, TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, InvalidBlockException? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token);
    void SetBlockAccessList(Block block);
    void ValidateBlockAccessList(Block block, ushort index, bool validateStorageReads = true);
    void StoreBeaconRoot(Block block, IReleaseSpec spec);
    void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec);
    void ProcessWithdrawals(Block block, IReleaseSpec spec);
    void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec);
    void ApplyAuRaPreprocessingChanges(IReleaseSpec spec, Address withdrawalContractAddress);
}
