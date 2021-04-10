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

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    /// <summary>
    /// For testing
    /// </summary>
    public class AlwaysCancelTxTracer : ITxTracer
    {
        private const string ErrorMessage = "Cancelling tracer invoked.";

        private static AlwaysCancelTxTracer _instance;

        private AlwaysCancelTxTracer()
        {
        }

        public static AlwaysCancelTxTracer Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new AlwaysCancelTxTracer()); }
        }

        public bool IsTracingReceipt => true;
        public bool IsTracingActions => true;
        public bool IsTracingOpLevelStorage => true;
        public bool IsTracingMemory => true;
        public bool IsTracingInstructions => true;
        public bool IsTracingRefunds => true;
        public bool IsTracingCode => true;
        public bool IsTracingStack => true;
        public bool IsTracingState => true;
        public bool IsTracingStorage => true;
        public bool IsTracingBlockHash => true;
        public bool IsTracingAccess => true;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null) => throw new OperationCanceledException(ErrorMessage);

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null) => throw new OperationCanceledException(ErrorMessage);

        public void StartOperation(int depth, long gas, Instruction opcode, int pc) => throw new OperationCanceledException(ErrorMessage);

        public void ReportOperationError(EvmExceptionType error) => throw new OperationCanceledException(ErrorMessage);

        public void ReportOperationRemainingGas(long gas) => throw new OperationCanceledException(ErrorMessage);

        public void SetOperationMemorySize(ulong newSize) => throw new OperationCanceledException(ErrorMessage);
        
        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data) => throw new OperationCanceledException(ErrorMessage);
        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) => throw new OperationCanceledException(ErrorMessage);

        public void SetOperationStack(List<string> stackTrace) => throw new OperationCanceledException(ErrorMessage);
        
        public void ReportStackPush(in ReadOnlySpan<byte> stackItem) => throw new OperationCanceledException(ErrorMessage);

        public void SetOperationMemory(List<string> memoryTrace) => throw new OperationCanceledException(ErrorMessage);

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) => throw new OperationCanceledException(ErrorMessage);
        
        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) => throw new OperationCanceledException(ErrorMessage);

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) => throw new OperationCanceledException(ErrorMessage);

        public void ReportCodeChange(Address address, byte[] before, byte[] after) => throw new OperationCanceledException(ErrorMessage);

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after) => throw new OperationCanceledException(ErrorMessage);
        
        public void ReportAccountRead(Address address) => throw new OperationCanceledException(ErrorMessage);

        public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after) => throw new OperationCanceledException(ErrorMessage);
        
        public void ReportStorageRead(StorageCell storageCell) => throw new OperationCanceledException(ErrorMessage);

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) => throw new OperationCanceledException(ErrorMessage);

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output) => throw new OperationCanceledException(ErrorMessage);
        public void ReportActionError(EvmExceptionType exceptionType) => throw new OperationCanceledException(ErrorMessage);

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) => throw new OperationCanceledException(ErrorMessage);
        public void ReportBlockHash(Keccak blockHash) => throw new OperationCanceledException(ErrorMessage);

        public void ReportByteCode(byte[] byteCode) => throw new OperationCanceledException(ErrorMessage);
        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)=> throw new OperationCanceledException(ErrorMessage);
        public void ReportRefund(long refund) => throw new OperationCanceledException(ErrorMessage);
        public void ReportExtraGasPressure(long extraGasPressure) => throw new OperationCanceledException(ErrorMessage); 
        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells) => throw new OperationCanceledException(ErrorMessage);
    }
}
