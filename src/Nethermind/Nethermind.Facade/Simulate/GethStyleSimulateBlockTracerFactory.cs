// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Microsoft.ClearScript;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;

namespace Nethermind.Facade.Simulate;

public class GethStyleSimulateBlockTracerFactory(GethTraceOptions options) : ISimulateBlockTracerFactory<GethLikeTxTrace>
{
    public IBlockTracer<GethLikeTxTrace> CreateSimulateBlockTracer(bool isTracingLogs, IWorldState worldState, ISpecProvider spec, BlockHeader block) =>
        CreateTracer(worldState, spec, block, null);

    public IBlockTracer<GethLikeTxTrace> CreateSimulateBlockTracer(bool isTracingLogs, IWorldState worldState, ISpecProvider spec, BlockHeader block, CancellationToken cancellationToken) =>
        CreateTracer(worldState, spec, block, cancellationToken);

    /// <remarks>Simulation extensions retain their invalid-params response for tracer compilation errors.</remarks>
    private IBlockTracer<GethLikeTxTrace> CreateTracer(IWorldState worldState, ISpecProvider spec, BlockHeader block, CancellationToken? cancellationToken)
    {
        try
        {
            return cancellationToken is { } token
                ? GethStyleTracer.CreateOptionsTracer(block, options, worldState, spec, logIndex: null, token)
                : GethStyleTracer.CreateOptionsTracer(block, options, worldState, spec);
        }
        catch (InvalidDataException exception) when (exception.InnerException is ScriptEngineException { IsFatal: false })
        {
            throw new ArgumentException($"Tracer code could not be compiled: {exception.Message}", exception);
        }
    }
}
