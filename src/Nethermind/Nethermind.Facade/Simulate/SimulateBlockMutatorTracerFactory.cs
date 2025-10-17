// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;
public class SimulateBlockMutatorTracerFactory : ISimulateBlockTracerFactory<SimulateCallResult>
{
    public IBlockTracer<SimulateCallResult> CreateSimulateBlockTracer(bool isTracingLogs, IWorldState worldState, ISpecProvider spec, BlockHeader block) =>
        new SimulateBlockTracer(isTracingLogs);
}
