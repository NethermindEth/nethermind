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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Evm.Tracing
{
    public class CallTracer : ITxTracer
    {
        public bool IsTracingReceipt => true;
        public bool IsTracingCalls => false;
        public bool IsTracingStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingStack => false;

        public byte[] ReturnValue { get; set; }
        
        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] returnValue, LogEntry[] logs)
        {
            ReturnValue = returnValue;
        }

        public void MarkAsFailed(Address recipient, long gasSpent)
        {
            ReturnValue = Bytes.Empty;
        }

        public void StartOperation(int callDepth, long gas, Instruction opcode, int programCounter)
        {
            throw new NotSupportedException();
        }

        public void SetOperationError(string error)
        {
            throw new NotSupportedException();
        }

        public void SetOperationRemainingGas(long gas)
        {
            throw new NotSupportedException();
        }

        public void SetOperationStack(List<string> getStackTrace)
        {
            throw new NotSupportedException();
        }

        public void SetOperationMemory(List<string> getTrace)
        {
            throw new NotSupportedException();
        }

        public void UpdateMemorySize(ulong memorySize)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageChange(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue, long cost, long refund)
        {
            throw new NotSupportedException();
        }
    }
}