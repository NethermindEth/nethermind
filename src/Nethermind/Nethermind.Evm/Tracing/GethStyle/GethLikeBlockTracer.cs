﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethLikeBlockTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxTracer>
    {
        private readonly GethTraceOptions _options;
        private readonly CancellationToken _cancellationToken; 

        public GethLikeBlockTracer(GethTraceOptions options, CancellationToken cancellationToken = default(CancellationToken))
        {
            _cancellationToken = cancellationToken;
            _options = options;
        }
        
        public GethLikeBlockTracer(Keccak txHash, GethTraceOptions options, CancellationToken cancellationToken = default(CancellationToken))
            : base(txHash)
        {
            _cancellationToken = cancellationToken;
            _options = options;
        }

        protected override GethLikeTxTracer OnStart(Keccak txHash)
        {
            return new GethLikeTxTracer(_options, _cancellationToken);
        }

        protected override GethLikeTxTrace OnEnd(GethLikeTxTracer txTracer)
        {
            return txTracer.BuildResult();
        }

        public override void StartNewBlockTrace(Block block)
        {
        }
    }
}