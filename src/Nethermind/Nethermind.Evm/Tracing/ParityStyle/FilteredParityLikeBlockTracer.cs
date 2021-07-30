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
// 

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class FilteredParityLikeBlockTracer : ParityLikeBlockTracer
    {
        private readonly TraceFilter _traceFilter;
        
        public FilteredParityLikeBlockTracer
            (Keccak txHash, ParityTraceTypes types, TraceFilter traceFilter) 
            : base(txHash, types)
        {
            _traceFilter = traceFilter;
        }

        public FilteredParityLikeBlockTracer(ParityTraceTypes types, TraceFilter traceFilter)
            : base(types)
        {
            _traceFilter = traceFilter;
        }
        
        protected override ParityLikeTxTracer OnStart(Transaction? tx) => 
            new(_block, tx, _types);
    }
}
