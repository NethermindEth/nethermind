// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;

namespace Nethermind.Facade.Simulate;
public class ParityStyleSimulateBlockTracerFactory(ParityTraceTypes types) : ISimulateBlockTracerFactory<ParityLikeTxTrace>
{
    public IBlockTracer<ParityLikeTxTrace> CreateSimulateBlockTracer(bool isTracingLogs, IWorldState worldState, ISpecProvider spec, BlockHeader block) =>
        new ParityLikeBlockTracer(types);
}
