// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Facade.Simulate;

public interface ISimulateBlockTracerFactory<out TTrace>
{
    public IBlockTracer<TTrace> CreateSimulateBlockTracer(bool isTracingLogs, IWorldState worldState, ISpecProvider spec, BlockHeader block);

    /// <summary>Creates the tracer for a simulation that <paramref name="cancellationToken"/> can stop, including while the tracer is created.</summary>
    public IBlockTracer<TTrace> CreateSimulateBlockTracer(bool isTracingLogs, IWorldState worldState, ISpecProvider spec, BlockHeader block, CancellationToken cancellationToken) =>
        CreateSimulateBlockTracer(isTracingLogs, worldState, spec, block);
}
