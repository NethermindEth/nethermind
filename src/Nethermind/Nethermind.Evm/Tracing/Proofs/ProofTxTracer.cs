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

namespace Nethermind.Evm.Tracing.Proofs
{
    public class ProofTxTracer : ITxTracer
    {
        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            throw new NotImplementedException();
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            throw new NotImplementedException();
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            throw new NotImplementedException();
        }

        public void ReportStorageChange(StorageAddress storageAddress, byte[] before, byte[] after)
        {
            throw new NotImplementedException();
        }

        public bool IsTracingReceipt => false;
        public bool IsTracingActions => false;
        public bool IsTracingOpLevelStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingCode => false;
        public bool IsTracingStack => false;
        public bool IsTracingState => false;
        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
        {
            throw new NotImplementedException();
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
        {
            throw new NotImplementedException();
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            throw new NotImplementedException();
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            throw new NotImplementedException();
        }

        public void ReportOperationRemainingGas(long gas)
        {
            throw new NotImplementedException();
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            throw new NotImplementedException();
        }

        public void ReportStackPush(Span<byte> stackItem)
        {
            throw new NotImplementedException();
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            throw new NotImplementedException();
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            throw new NotImplementedException();
        }

        public void ReportMemoryChange(long offset, Span<byte> data)
        {
            throw new NotImplementedException();
        }

        public void ReportStorageChange(Span<byte> key, Span<byte> value)
        {
            throw new NotImplementedException();
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue)
        {
            throw new NotImplementedException();
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            throw new NotImplementedException();
        }

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType, bool isPrecompileCall = false)
        {
            throw new NotImplementedException();
        }

        public void ReportActionEnd(long gas, byte[] output)
        {
            throw new NotImplementedException();
        }

        public void ReportActionError(EvmExceptionType evmExceptionType)
        {
            throw new NotImplementedException();
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, byte[] deployedCode)
        {
            throw new NotImplementedException();
        }

        public void ReportByteCode(byte[] byteCode)
        {
            throw new NotImplementedException();
        }

        public void ReportRefundForVmTrace(long refund, long gasAvailable)
        {
            throw new NotImplementedException();
        }

        public void ReportRefund(long refund)
        {
            throw new NotImplementedException();
        }
    }
}