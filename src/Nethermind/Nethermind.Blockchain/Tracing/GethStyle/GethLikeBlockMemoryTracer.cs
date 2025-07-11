// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeBlockMemoryTracer(GethTraceOptions options)
    : BlockTracerBase<GethLikeTxTrace, GethLikeTxMemoryTracer>(options.TxHash)
{
    protected override GethLikeTxMemoryTracer OnStart(Transaction? tx) => new(tx, options);

    protected override GethLikeTxTrace OnEnd(GethLikeTxMemoryTracer txTracer) => txTracer.BuildResult();
}
