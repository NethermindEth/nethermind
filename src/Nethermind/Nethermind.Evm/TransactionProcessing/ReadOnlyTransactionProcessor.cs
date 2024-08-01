// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public class ReadOnlyTransactionProcessor : IReadOnlyTransactionProcessor
    {
        public IWorldState WorldState { get; set; }
        private readonly ITransactionProcessor _transactionProcessor;
        private Hash256 _stateBefore;

        public ReadOnlyTransactionProcessor(ITransactionProcessor transactionProcessor, IWorldState worldState, Hash256 startState)
        {
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            WorldState = worldState;
            _stateBefore = worldState.StateRoot;
            WorldState.StateRoot = startState;
        }

        public TransactionResult Execute(Transaction transaction, IWorldState worldState, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            _transactionProcessor.Execute(transaction, worldState, in blCtx, txTracer);

        public TransactionResult CallAndRestore(Transaction transaction, IWorldState worldState, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            _transactionProcessor.CallAndRestore(transaction, worldState, in blCtx, txTracer);

        public TransactionResult BuildUp(Transaction transaction, IWorldState worldState, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            _transactionProcessor.BuildUp(transaction, worldState, in blCtx, txTracer);

        public TransactionResult Trace(Transaction transaction, IWorldState worldState, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            _transactionProcessor.Trace(transaction, worldState, in blCtx, txTracer);

        public bool IsContractDeployed(Address address)
        {
            return WorldState.IsContract(address);
        }

        public void Dispose()
        {
            WorldState.StateRoot = _stateBefore;
            WorldState.Reset();
        }

    }
}
