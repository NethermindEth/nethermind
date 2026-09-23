// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// System-contract and validator-orchestration bridges. Each helper routes its work through
/// the appropriate worldstate pulled from the tx-processor pool — pre-execution callers
/// (beacon root, blockhash) use the pre slot; post-execution callers (withdrawals,
/// execution requests) use the post slot.
/// </summary>
public partial class BlockAccessListManager
{
    public void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        CheckInitialized();

        TxProcessorWithWorldState preExecution = _txProcessorWithWorldStateManager.GetPreExecution();
        new BeaconBlockRootHandler(preExecution.TxProcessor, preExecution.WorldState).StoreBeaconRoot(block, spec, NullTxTracer.Instance);
    }

    public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec)
    {
        CheckInitialized();

        if (!spec.IsEip2935Enabled || header.IsGenesis || header.ParentHash is null) return;

        TxProcessorWithWorldState preExecution = _txProcessorWithWorldStateManager.GetPreExecution();
        Address historyAddress = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        if (!preExecution.WorldState.IsContract(historyAddress)) return;

        // EIP-2935 runs whatever code the account holds; a direct storage write matches only the canonical bytecode.
        Transaction transaction = spec.IsEip8037Enabled
            ? new SystemCall { GasLimit = Eip8037Constants.SystemCallGasLimit }
            : new Transaction { GasLimit = Eip8037Constants.SystemCallBaseGasLimit };
        transaction.Data = header.ParentHash.Bytes.ToArray();
        transaction.To = historyAddress;
        transaction.SenderAddress = Address.SystemUser;
        preExecution.TxProcessor.Execute(transaction, NullTxTracer.Instance);
    }

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        CheckInitialized();

        TxProcessorWithWorldState postExecution = _txProcessorWithWorldStateManager.GetPostExecution();
        IWithdrawalProcessor withdrawalProcessor = withdrawalProcessorFactory.Create(postExecution.WorldState, postExecution.TxProcessor);
        if (_isBuilding)
        {
            withdrawalProcessor = new BlockProductionWithdrawalProcessor(withdrawalProcessor);
        }
        withdrawalProcessor.ProcessWithdrawals(block, spec);
    }

    public void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)
    {
        CheckInitialized();

        TxProcessorWithWorldState postExecution = _txProcessorWithWorldStateManager.GetPostExecution();
        IExecutionRequestsProcessor executionRequestsProcessor =
            (executionRequestsProcessorFactory ?? ExecutionRequestsProcessorFactory.Instance).Create(postExecution.TxProcessor);
        executionRequestsProcessor.ProcessExecutionRequests(block, postExecution.WorldState, txReceipts, spec);
    }
}
