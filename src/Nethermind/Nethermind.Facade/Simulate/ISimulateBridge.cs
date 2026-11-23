// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

/// <summary>Supplies the base state and execution for a simulated chain of blocks.</summary>
public interface ISimulateBridge
{
    /// <summary>Gets the chain ID used by simulated transactions.</summary>
    ulong GetChainId();

    /// <summary>Checks whether the specified base block can be used for simulation.</summary>
    bool HasStateForBlock(BlockHeader? baseBlock);

    /// <summary>Executes and traces the simulated blocks on top of the supplied header.</summary>
    SimulateOutput<TTrace> Simulate<TTrace>(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload,
        ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory, ulong gasCapLimit, CancellationToken cancellationToken);
}
