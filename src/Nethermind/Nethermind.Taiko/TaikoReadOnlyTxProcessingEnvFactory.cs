// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoReadOnlyTxProcessingEnvFactory(
    IWorldStateManager worldStateManager,
    IReadOnlyBlockTree readOnlyBlockTree,
    ISpecProvider specProvider,
    ILogManager logManager,
    IWorldState? worldStateToWarmUp = null)
{
    public ReadOnlyTxProcessingEnv Create() => new TaikoReadOnlyTxProcessingEnv(worldStateManager, readOnlyBlockTree, specProvider, logManager, worldStateToWarmUp);
}
