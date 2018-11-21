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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Tracing
{
    public interface ITxTracer
    {
        bool IsTracingReceipt { get; }
        bool IsTracingCalls { get; }
        bool IsTracingStorage { get; }
        bool IsTracingMemory { get; }
        bool IsTracingInstructions { get; }
        bool IsTracingStack { get; }

        void MarkAsSuccess(Address recipient, long gasSpent, byte[] returnValue, LogEntry[] logs);
        void MarkAsFailed(Address recipient, long gasSpent);

        void StartOperation(int callDepth, long gas, Instruction opcode, int programCounter);
        void SetOperationError(string error);
        void SetOperationRemainingGas(long gas);
        void SetOperationStack(List<string> getStackTrace);
        void SetOperationMemory(List<string> getTrace);
        void UpdateMemorySize(ulong memorySize);
        void ReportStorageChange(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue, long cost, long refund);
    }
}