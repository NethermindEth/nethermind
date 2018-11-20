/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.Tracing
{
    public class GethLikeTxTracer : ITxTracer
    {
        private GethLikeTxTrace _trace = new GethLikeTxTrace();
        
        public bool IsTracing => true;
        bool ITxTracer.IsTracingCalls => true;
        bool ITxTracer.IsTracingStorage => true;
        bool ITxTracer.IsTracingMemory => true;
        bool ITxTracer.IsTracingOpcodes => true;
        bool ITxTracer.IsTracingStack => true;
        public void MarkAsFailed()
        {
            _trace.Failed = true;
        }

        public void SetReturnValue(byte[] returnValue)
        {
            _trace.ReturnValue = returnValue?.ToHexString();
        }

        public void SetGasSpent(ulong gasSpent)
        {
            _trace.Gas = gasSpent;
        }

        public GethLikeTxTrace BuildResult()
        {
            return _trace;
        }
    }
    
    public class GethLikeTxTrace
    {
        public List<Dictionary<string, string>> StoragesByDepth { get; } = new List<Dictionary<string, string>>();

        public GethLikeTxTrace()
        {
            Entries = new List<TransactionTraceEntry>();
            StorageTrace = new StorageTrace();
        }

        public StorageTrace StorageTrace { get; set; }
        
        public BigInteger Gas { get; set; }

        public bool Failed { get; set; }

        public string ReturnValue { get; set; }
        
        public List<TransactionTraceEntry> Entries { get; set; }

        public static GethLikeTxTrace QuickFail { get; } = new GethLikeTxTrace {Failed = true, ReturnValue = string.Empty};
    }
}