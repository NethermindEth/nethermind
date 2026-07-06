// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class AutoReadOnlyTxProcessingEnvFactory(IProcessingEnvBuilder envBuilder, IWorldStateManager worldStateManager) : IReadOnlyTxProcessingEnvFactory
{
    public IReadOnlyTxProcessorSource Create() =>
        new AutoReadOnlyTxProcessingEnv(envBuilder
            .WithWorldState(worldStateManager.CreateResettableWorldState())
            .BuildAs<AutoReadOnlyTxProcessingEnv.IEnv>());

    public class AutoReadOnlyTxProcessingEnv(AutoReadOnlyTxProcessingEnv.IEnv env) : IReadOnlyTxProcessorSource
    {
        public interface IEnv : IDisposable
        {
            ITransactionProcessor TransactionProcessor { get; }
            IWorldState WorldState { get; }
        }

        public IReadOnlyTxProcessingScope Build(BlockHeader? header) =>
            new ReadOnlyTxProcessingScope(env.TransactionProcessor, env.WorldState.BeginScope(header), env.WorldState);

        public void Dispose() => env.Dispose();
    }
}
