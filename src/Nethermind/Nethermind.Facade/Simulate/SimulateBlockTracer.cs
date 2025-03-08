// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockTracer(bool isTracingLogs, bool includeFullTxData, ISpecProvider spec) : SimulateBlockTracerBase<SimulateTxMutatorTracer, SimulateCallResult>(isTracingLogs, includeFullTxData, spec), IBlockTracer<SimulateBlockResult<SimulateCallResult>>
{
    public override ITxTracer StartNewTxTrace(Transaction? tx)
    {

        if (tx?.Hash is not null)
        {
            ulong txIndex = (ulong)_txTracers.Count;
            SimulateTxMutatorTracer result = new(_isTracingLogs, tx.Hash, (ulong)_currentBlock.Number,
                _currentBlock.Hash, txIndex);
            _txTracers.Add(result);
            return result;
        }

        return NullTxTracer.Instance;
    }

    protected override IReadOnlyList<SimulateCallResult> GetCalls()
    {
        return _txTracers.Select(t => t.TraceResult).ToList();
    }
}

public class SimulateBlockTracerFactory : ISimulateBlockTracerBaseFactory<SimulateBlockTracer, SimulateTxMutatorTracer, SimulateCallResult>
{
    public SimulateBlockTracer Create(bool isTracingLogs, bool includeFullTxData, ISpecProvider spec) => new(isTracingLogs, includeFullTxData, spec);
}
