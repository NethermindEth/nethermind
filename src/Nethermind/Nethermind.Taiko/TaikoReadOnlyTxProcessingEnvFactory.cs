// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoReadOnlyTxProcessingEnvFactory(
    IWorldStateManager worldStateManager,
    IReadOnlyBlockTree readOnlyBlockTree,
    ISpecProvider specProvider,
    ILogManager logManager): IReadOnlyTxProcessingEnvFactory
{
    public IReadOnlyTxProcessorSource Create() => new TaikoReadOnlyTxProcessingEnv (worldStateManager, readOnlyBlockTree, specProvider, logManager);
    public IReadOnlyTxProcessorSource CreateForWarmingUp(IWorldState worldStateForWarmUp) => new TaikoReadOnlyTxProcessingEnv(worldStateManager, readOnlyBlockTree, specProvider, logManager, worldStateForWarmUp);
}
