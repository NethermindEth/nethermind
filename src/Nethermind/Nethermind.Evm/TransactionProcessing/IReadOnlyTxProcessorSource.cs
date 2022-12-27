// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface IReadOnlyTxProcessorSource
    {

        public IStateReader StateReader { get; }
        public IStateProvider StateProvider { get; }
        public IStorageProvider StorageProvider { get; }
        public ITransactionProcessor TransactionProcessor { get; set; }
        public IBlockhashProvider BlockhashProvider { get; }
        public IVirtualMachine Machine { get; }

        IReadOnlyTransactionProcessor Build(Keccak stateRoot);
    }
}
