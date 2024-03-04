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
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IWorldState _stateProvider;
        private readonly Hash256 _stateBefore;

        public ReadOnlyTransactionProcessor(ITransactionProcessor transactionProcessor, IWorldState stateProvider, Hash256 startState)
        {
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _stateBefore = _stateProvider.StateRoot;
            _stateProvider.StateRoot = startState ?? throw new ArgumentNullException(nameof(startState));
        }

        public TransactionResult Execute(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            _transactionProcessor.Execute(transaction, in blCtx, txTracer);

        public TransactionResult CallAndRestore(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            _transactionProcessor.CallAndRestore(transaction, in blCtx, txTracer);

        public TransactionResult BuildUp(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            _transactionProcessor.BuildUp(transaction, in blCtx, txTracer);

        public TransactionResult Trace(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            _transactionProcessor.Trace(transaction, in blCtx, txTracer);


        public bool IsContractDeployed(Address address) => _stateProvider.IsContract(address);

        public void Dispose()
        {
            _stateProvider.StateRoot = _stateBefore;
            _stateProvider.Reset();
        }
    }
}
