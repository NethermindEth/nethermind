// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface ITransactionProcessorAdapter
    {
        TransactionResult Execute(Transaction transaction, in BlockExecutionContext blkCtx, ITxTracer txTracer);

        public ITransactionProcessorAdapter WithNewStateProvider(IWorldState worldState);
    }
}
