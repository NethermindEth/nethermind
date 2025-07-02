// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.State;

namespace Nethermind.Facade.Simulate;
public class GethStyleSimulateBlockTracerFactory(GethTraceOptions options) : ISimulateBlockTracerFactory<GethLikeTxTrace>
{
    public IBlockTracer<GethLikeTxTrace> CreateSimulateBlockTracer(bool isTracingLogs, IWorldState worldState, ISpecProvider spec, BlockHeader block) =>
        GethStyleTracer.CreateOptionsTracer(block, options, worldState, spec);
}
