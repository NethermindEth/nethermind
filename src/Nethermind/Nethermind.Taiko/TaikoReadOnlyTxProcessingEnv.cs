// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoReadOnlyTxProcessingEnv : ReadOnlyTxProcessingEnv
{
    public TaikoReadOnlyTxProcessingEnv(
        IWorldStateManager worldStateManager,
        IReadOnlyBlockTree readOnlyBlockTree,
        ISpecProvider specProvider,
        ILogManager logManager,
        IWorldState worldStateToWarmUp) : base(worldStateManager, readOnlyBlockTree, specProvider, logManager, worldStateToWarmUp)
    {
    }

    public TaikoReadOnlyTxProcessingEnv(IWorldStateManager worldStateManager,
        IReadOnlyBlockTree readOnlyBlockTree,
        ISpecProvider specProvider,
        ILogManager logManager) : base(worldStateManager,
        readOnlyBlockTree,
        specProvider,
        logManager)
    {
    }

    protected override ITransactionProcessor CreateTransactionProcessor() =>
        new TaikoTransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, LogManager);
}
