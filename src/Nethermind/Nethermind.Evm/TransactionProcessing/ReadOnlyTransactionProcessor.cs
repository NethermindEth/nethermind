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
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ReadOnlyDb _codeDb;
        private readonly Keccak _stateBefore;

        public ReadOnlyTransactionProcessor(ITransactionProcessor transactionProcessor, IStateProvider stateProvider, IStorageProvider storageProvider, ReadOnlyDb codeDb, Keccak startState)
        {
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _stateBefore = _stateProvider.StateRoot;
            _stateProvider.StateRoot = startState ?? throw new ArgumentNullException(nameof(startState));
        }

        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.Execute(transaction, block, txTracer);

        public void CallAndRestore(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.CallAndRestore(transaction, block, txTracer);

        public void BuildUp(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.BuildUp(transaction, block, txTracer);

        public void Trace(Transaction transaction, BlockHeader block, ITxTracer txTracer) =>
            _transactionProcessor.Trace(transaction, block, txTracer);


        public bool IsContractDeployed(Address address) => _stateProvider.IsContract(address);

        public void Dispose()
        {
            _stateProvider.StateRoot = _stateBefore;
            _stateProvider.Reset();
            _storageProvider.Reset();
            _codeDb.ClearTempChanges();
        }
    }
}
