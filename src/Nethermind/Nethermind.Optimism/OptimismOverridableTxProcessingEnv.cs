// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OptimismOverridableTxProcessingEnv(
    IOverridableWorldScope worldStateManager,
    IReadOnlyBlockTree readOnlyBlockTree,
    ISpecProvider specProvider,
    ILogManager logManager,
    ICostHelper costHelper,
    IOptimismSpecHelper opSpecHelper)
    : OverridableTxProcessingEnv(worldStateManager, readOnlyBlockTree, specProvider, logManager)
{
    protected override ITransactionProcessor CreateTransactionProcessor()
    {
        ArgumentNullException.ThrowIfNull(LogManager);

        BlockhashProvider blockhashProvider = new(BlockTree, SpecProvider, StateProvider, LogManager);
        VirtualMachine virtualMachine = new(blockhashProvider, SpecProvider, LogManager);
        return new OptimismTransactionProcessor(SpecProvider, StateProvider, virtualMachine, LogManager, costHelper, opSpecHelper, CodeInfoRepository);
    }
}
