// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Collects execution witness for a single call (not a full block).
/// Used by taiko_executionWitnessCall to capture state accessed during a single eth_call.
/// </summary>
public interface ISingleCallWitnessCollector
{
    /// <summary>
    /// Execute a single call at the given block's state and collect the execution witness.
    /// </summary>
    /// <param name="blockHeader">Block whose post-state to execute against</param>
    /// <param name="transaction">The call transaction to execute</param>
    /// <returns>Execution witness containing all state data accessed during the call</returns>
    Witness ExecuteCallAndCollectWitness(BlockHeader blockHeader, Transaction transaction);
}

public class SingleCallWitnessCollector(
    WitnessGeneratingWorldState worldState,
    ITransactionProcessor transactionProcessor) : ISingleCallWitnessCollector
{
    public Witness ExecuteCallAndCollectWitness(BlockHeader blockHeader, Transaction transaction)
    {
        // Uses blockHeader (not parentHeader) intentionally: for a single call we want the
        // post-state of the target block. Block-level witness uses parentHeader because it
        // needs the pre-state to re-execute the block's transactions.
        using IDisposable? scope = worldState.BeginScope(blockHeader);

        // Output is intentionally discarded — we only need the witness (state access record),
        // not the call result. Even if the call reverts, the witness captures all accessed state.
        CallOutputTracer tracer = new();
        transactionProcessor.CallAndRestore(transaction, blockHeader, tracer);

        return worldState.GetWitness(blockHeader);
    }
}
