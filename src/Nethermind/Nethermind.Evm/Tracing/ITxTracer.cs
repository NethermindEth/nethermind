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
using Nethermind.Store;

namespace Nethermind.Evm.Tracing
{
    public interface ITxTracer : IStateTracer, IStorageTracer
    {
        bool IsTracingReceipt { get; }
        bool IsTracingCalls { get; }
        bool IsTracingOpLevelStorage { get; }
        bool IsTracingMemory { get; }
        bool IsTracingInstructions { get; }
        bool IsTracingStack { get; }
        bool IsTracingState { get; }

        void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs);
        void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error);

        void StartOperation(int depth, long gas, Instruction opcode, int pc);
        void SetOperationError(string error);
        void SetOperationRemainingGas(long gas);
        void SetOperationStack(List<string> stackTrace);
        void SetOperationMemory(List<string> memoryTrace);
        void SetOperationMemorySize(ulong newSize);
        void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue);
        
        void ReportCall(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType);
        void ReportCallEnd(long gas, byte[] output);
        void ReportCreateEnd(long gas, Address deploymentAddress, byte[] deployedCode);
    }
}