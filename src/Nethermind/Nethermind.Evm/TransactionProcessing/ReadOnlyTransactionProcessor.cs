// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public class ReadOnlyTransactionProcessor : IReadOnlyTransactionProcessor
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IWorldState _worldState;
        private readonly Keccak _stateBefore;

        public ReadOnlyTransactionProcessor(ITransactionProcessor transactionProcessor, IWorldState worldState, Keccak startState)
        {
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _worldState = worldState;
            _stateBefore = _worldState.StateRoot;
            _worldState.StateRoot = startState ?? throw new ArgumentNullException(nameof(startState));
        }

        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.Execute(transaction, block, txTracer);

        public void CallAndRestore(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.CallAndRestore(transaction, block, txTracer);

        public void BuildUp(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.BuildUp(transaction, block, txTracer);

        public void Trace(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.Trace(transaction, block, txTracer);


        public bool IsContractDeployed(Address address) => _worldState.IsContract(address);

        public void Dispose()
        {
            _worldState.StateRoot = _stateBefore;
            _worldState.Reset();
        }
    }
}
