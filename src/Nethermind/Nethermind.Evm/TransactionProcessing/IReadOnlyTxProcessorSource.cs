// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface IReadOnlyTxProcessorSource
    {
        IReadOnlyTransactionProcessor Build(Block block, Hash256 stateRoot);
        IReadOnlyTransactionProcessor Build(BlockHeader header, Hash256 stateRoot);
        public IReadOnlyTransactionProcessor Build(IWorldState worldState, Hash256 stateRoot);
    }
}
