// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

public class ParityLikeSimulateBlockTracer(bool isTracingLogs, bool includeFullTxData, ISpecProvider spec) : SimulateBlockTracerBase<ParityLikeTxTracer, ParityLikeTxTrace>(isTracingLogs, includeFullTxData, spec), IBlockTracer<SimulateBlockResult<ParityLikeTxTrace>>
{
    public override ITxTracer StartNewTxTrace(Transaction? tx)
    {

        if (tx?.Hash is not null)
        {
            ParityLikeTxTracer result = new(_currentBlock, tx, ParityTraceTypes.All);
            _txTracers.Add(result);
            return result;
        }

        return NullTxTracer.Instance;
    }

    protected override IReadOnlyList<ParityLikeTxTrace> GetCalls()
    {
        return _txTracers.Select(t => t.BuildResult()).ToList();
    }
}
