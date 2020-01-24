//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Evm.Tracing
{
    public interface ITxTracer : IStateTracer, IStorageTracer
    {
        bool IsTracingReceipt { get; }
        bool IsTracingActions { get; }
        bool IsTracingOpLevelStorage { get; }
        bool IsTracingMemory { get; }
        bool IsTracingInstructions { get; }
        bool IsTracingCode { get; }
        bool IsTracingStack { get; }
        bool IsTracingState { get; }

        void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
        {
            throw new NotImplementedException();
        }

        void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
        {
            throw new NotImplementedException();
        }

        void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            throw new NotImplementedException();
        }

        void ReportOperationError(EvmExceptionType error)
        {
            throw new NotImplementedException();
        }

        void ReportOperationRemainingGas(long gas)
        {
            throw new NotImplementedException();
        }

        void SetOperationStack(List<string> stackTrace)
        {
            throw new NotImplementedException();
        }

        void ReportStackPush(Span<byte> stackItem)
        {
            throw new NotImplementedException();
        }

        void SetOperationMemory(List<string> memoryTrace)
        {
            throw new NotImplementedException();
        }

        void SetOperationMemorySize(ulong newSize)
        {
            throw new NotImplementedException();
        }

        void ReportMemoryChange(long offset, Span<byte> data)
        {
            throw new NotImplementedException();
        }

        void ReportStorageChange(Span<byte> key, Span<byte> value)
        {
            throw new NotImplementedException();
        }

        void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue)
        {
            throw new NotImplementedException();
        }

        void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            throw new NotImplementedException();
        }

        void ReportAction(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType, bool isPrecompileCall = false)
        {
            throw new NotImplementedException();
        }

        void ReportActionEnd(long gas, byte[] output)
        {
            throw new NotImplementedException();
        }

        void ReportActionError(EvmExceptionType evmExceptionType)
        {
            throw new NotImplementedException();
        }

        void ReportActionEnd(long gas, Address deploymentAddress, byte[] deployedCode)
        {
            throw new NotImplementedException();
        }

        void ReportByteCode(byte[] byteCode)
        {
            throw new NotImplementedException();
        }

        void ReportRefundForVmTrace(long refund, long gasAvailable)
        {
            throw new NotImplementedException();
        }

        void ReportRefund(long refund)
        {
            throw new NotImplementedException();
        }
    }
}