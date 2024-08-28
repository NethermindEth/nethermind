// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface IReadOnlyTxProcessorSource
    {
        IReadOnlyTransactionProcessor Build(Hash256 stateRoot, Block block);
        IReadOnlyTransactionProcessor Build(Hash256 stateRoot, BlockHeader header);
        public IReadOnlyTransactionProcessor Build(Hash256 stateRoot, IWorldState worldState);
    }
}
