// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

public class GethLikeSimulateBlockTracer(bool isTracingLogs, bool includeFullTxData, ISpecProvider spec) : SimulateBlockTracerBase<GethLikeTxMemoryTracer, GethLikeTxTrace>(isTracingLogs, includeFullTxData, spec), IBlockTracer<SimulateBlockResult<GethLikeTxTrace>>
{
    public override ITxTracer StartNewTxTrace(Transaction? tx)
    {

        if (tx?.Hash is not null)
        {
            GethLikeTxMemoryTracer result = new(tx, GethTraceOptions.Default);
            _txTracers.Add(result);
            return result;
        }

        return NullTxTracer.Instance;
    }

    protected override IReadOnlyList<GethLikeTxTrace> getCalls()
    {
        return _txTracers.Select(t => t.BuildResult()).ToList();
    }
}
