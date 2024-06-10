// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using System;

namespace Nethermind.Optimism;

public class OptimismReadOnlyTxProcessingEnv(
      IWorldStateManager worldStateManager,
      IReadOnlyBlockTree readOnlyBlockTree,
      ISpecProvider specProvider,
      ILogManager logManager,
      IL1CostHelper l1CostHelper,
      IOptimismSpecHelper opSpecHelper,
      IWorldState? worldStateToWarmUp = null) : ReadOnlyTxProcessingEnv(
      worldStateManager,
      readOnlyBlockTree,
      specProvider,
      logManager,
      worldStateToWarmUp
     )
{
    protected override TransactionProcessor CreateTransactionProcessor()
    {
        ArgumentNullException.ThrowIfNull(_logManager);

        BlockhashProvider blockhashProvider = new(BlockTree, _specProvider, StateProvider, _logManager);
        VirtualMachine virtualMachine = new(blockhashProvider, _specProvider, _logManager);
        return new OptimismTransactionProcessor(_specProvider, StateProvider, virtualMachine, _logManager, l1CostHelper, opSpecHelper);
    }
}
