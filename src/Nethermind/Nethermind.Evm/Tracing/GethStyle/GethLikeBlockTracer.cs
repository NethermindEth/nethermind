//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

        public override void StartNewBlockTrace(Block block)
        {
        }

        public override void EndBlockTrace()
        {
        }
    }
}
