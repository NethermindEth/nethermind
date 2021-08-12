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

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State
{
    public class NullStateTracer : IStateTracer
    {
        private NullStateTracer() { }

        public static IStateTracer Instance { get; } = new NullStateTracer();
        private const string ErrorMessage = "Null tracer should never receive any calls.";
        
        public bool IsTracingState => false;
        
        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportAccountRead(Address address)
            => throw new InvalidOperationException(ErrorMessage);
    }
}
