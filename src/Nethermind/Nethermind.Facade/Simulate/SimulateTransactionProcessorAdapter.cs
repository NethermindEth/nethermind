// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Facade.Simulate;

public class SimulateTransactionProcessorAdapter(ITransactionProcessor transactionProcessor, SimulateRequestState simulateRequestState) : ITransactionProcessorAdapter
{
    private int _currentTxIndex = 0;
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        // The gas limit per tx go down as the block is processed.
        if (!simulateRequestState.TxsWithExplicitGas[_currentTxIndex])
        {
            transaction.GasLimit = simulateRequestState.TotalGasLeft;
        }
        transaction.Hash = transaction.CalculateHash();

        TransactionResult result = simulateRequestState.Validate ? transactionProcessor.Execute(transaction, txTracer) : transactionProcessor.Trace(transaction, txTracer);

        // Keep track of gas left
        simulateRequestState.TotalGasLeft -= transaction.SpentGas;
        _currentTxIndex++;
        return result;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _currentTxIndex = 0;
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }
}
