// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OptimismBlockProductionTransactionPicker : BlockProcessor.BlockProductionTransactionPicker
{
    public OptimismBlockProductionTransactionPicker(ISpecProvider specProvider, long maxTxLengthKilobytes) : base(specProvider, maxTxLengthKilobytes)
    {
    }

    public override BlockProcessor.AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx,
        IReadOnlySet<Transaction> transactionsInBlock, IWorldState stateProvider)
    {
        if (!currentTx.IsDeposit())
            return base.CanAddTransaction(block, currentTx, transactionsInBlock, stateProvider);

        // Trusting CL with deposit tx validation
        BlockProcessor.AddingTxEventArgs args = new(transactionsInBlock.Count, currentTx, block, transactionsInBlock);
        OnAddingTransaction(args);
        return args;
    }
}
