// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoReadOnlyTxProcessingEnvFactory(
    OverridableWorldStateManager worldStateManager,
    IReadOnlyBlockTree readOnlyBlockTree,
    ISpecProvider specProvider,
    ILogManager logManager,
    IWorldState? worldStateToWarmUp = null)
{
    public TaikoReadOnlyTxProcessingEnv Create() => new (worldStateManager, readOnlyBlockTree, specProvider, logManager, worldStateToWarmUp);
}
