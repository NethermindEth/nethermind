// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethLikeBlockTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxTracer>
    {
        private readonly GethTraceOptions _options;

        public GethLikeBlockTracer(GethTraceOptions options)
        {
            _options = options;
        }

        public GethLikeBlockTracer(Keccak txHash, GethTraceOptions options)
            : base(txHash)
        {
            _options = options;
        }

        protected override GethLikeTxTracer OnStart(Transaction? tx) => new(_options);

        protected override GethLikeTxTrace OnEnd(GethLikeTxTracer txTracer) => txTracer.BuildResult();
    }
}
