// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Stateless;

/// <summary>Output of <see cref="ISingleCallWitnessCollector.ExecuteCallAndCollectWitness"/>.</summary>
/// <param name="Output">EVM return data — non-null on success (possibly empty for void returns) and on revert (revert payload); null when the call produced no return data (out-of-gas, invalid jump, input error).</param>
/// <param name="Error">Non-null when the call failed for any reason other than a plain success (revert is signalled separately via <paramref name="ExecutionReverted"/>).</param>
/// <param name="ExecutionReverted">True iff the EVM exited via REVERT; mirrors <c>CallOutput.ExecutionReverted</c>.</param>
/// <param name="InputError">True iff the transaction never executed (pre-VM validation failure); mirrors <c>CallOutput.InputError</c>.</param>
/// <param name="Witness">Execution witness — captured regardless of call outcome.</param>
public sealed record SingleCallWitnessResult(
    byte[]? Output,
    string? Error,
    bool ExecutionReverted,
    bool InputError,
    Witness Witness);

/// <summary>
/// Collects execution witness for a single call (not a full block).
/// Used by proof_call to capture state accessed during a single eth_call.
/// </summary>
public interface ISingleCallWitnessCollector
{
    /// <summary>
    /// Execute a single call at the given block's state and collect the execution witness.
    /// </summary>
    /// <param name="blockHeader">Block whose post-state to execute against</param>
    /// <param name="transaction">The call transaction to execute</param>
    /// <param name="cancellationToken">Aborts the EVM run if it outlives the caller's deadline (e.g. the RPC timeout).</param>
    /// <returns>The call's return data, eth_call-style success/error flags, and the execution witness containing all state data accessed during the call.</returns>
    SingleCallWitnessResult ExecuteCallAndCollectWitness(BlockHeader blockHeader, Transaction transaction, CancellationToken cancellationToken = default);
}

public class SingleCallWitnessCollector(
    WitnessGeneratingWorldState worldState,
    ITransactionProcessor transactionProcessor) : ISingleCallWitnessCollector
{
    public SingleCallWitnessResult ExecuteCallAndCollectWitness(BlockHeader blockHeader, Transaction transaction, CancellationToken cancellationToken = default)
    {
        // Uses blockHeader (not parentHeader) intentionally: for a single call we want the
        // post-state of the target block. Block-level witness uses parentHeader because it
        // needs the pre-state to re-execute the block's transactions.
        using IDisposable? scope = worldState.BeginScope(blockHeader);

        // Mirror BlockchainBridge.CallAndRestore: ignore the caller-supplied nonce and resolve it
        // from the scoped state. Without this, a proof_call request that includes `from` but omits
        // `nonce` fails pre-VM validation (e.g. TransactionNonceTooHigh) before the EVM runs, and
        // diverges from eth_call which performs the same ignore-nonce step.
        transaction.Nonce = worldState.GetNonce(transaction.SenderAddress!);

        // Even on revert, the witness captures all accessed state and the tracer records the revert
        // payload via MarkAsFailed. WithCancellation aborts a run that outlives the caller's deadline:
        // the EVM throws OperationCanceledException, surfaced by JsonRpcService as a Timeout error.
        CallOutputTracer tracer = new();
        TransactionResult txResult = transactionProcessor.CallAndRestore(transaction, blockHeader, tracer.WithCancellation(cancellationToken));

        return new SingleCallWitnessResult(
            Output: tracer.ReturnValue,
            Error: txResult.GetErrorMessage(tracer.Error),
            ExecutionReverted: txResult.EvmExceptionType == EvmExceptionType.Revert,
            InputError: !txResult.TransactionExecuted,
            Witness: worldState.GetWitness(blockHeader));
    }
}
