// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class PrewarmerEnvFactory(IWorldStateManager worldStateManager, ILogManager logManager, Func<IProcessingEnvBuilder> envBuilder)
{
    public IReadOnlyTxProcessorSource Create(PreBlockCaches preBlockCaches) =>
        new AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv(
            envBuilder()
                .WithWorldState(new PrewarmerScopeProvider(
                    worldStateManager.CreateResettableWorldState(),
                    preBlockCaches,
                    logManager,
                    isPrewarmer: true))
                .BuildAs<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv.IEnv>());
}
