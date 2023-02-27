// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethLikeBlockMemoryTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxMemoryTracer>
    {
        private readonly GethTraceOptions _options;

        public GethLikeBlockMemoryTracer(GethTraceOptions options)
        {
            _options = options;
        }

        public GethLikeBlockMemoryTracer(Keccak txHash, GethTraceOptions options)
            : base(txHash)
        {
            _options = options;
        }

        protected override GethLikeTxMemoryTracer OnStart(Transaction? tx) => new(_options);

        protected override GethLikeTxTrace OnEnd(GethLikeTxMemoryTracer txTracer) => txTracer.BuildResult();
    }
}
