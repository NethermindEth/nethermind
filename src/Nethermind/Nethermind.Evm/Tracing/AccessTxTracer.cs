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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class AccessTxTracer : ITxTracer
    {
        private const long ColdVsWarmSloadDelta = GasCostOf.ColdSLoad - GasCostOf.AccessStorageListEntry;
        public const long MaxStorageAccessToOptimize = GasCostOf.AccessAccountListEntry / ColdVsWarmSloadDelta;
        
        private readonly Address[] _addressesToOptimize;
        
        public bool IsTracingState => false;
        public bool IsTracingStorage => false;
        public bool IsTracingReceipt => true;
        public bool IsTracingActions => false;
        public bool IsTracingOpLevelStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingRefunds => false;
        public bool IsTracingCode => false;
        public bool IsTracingStack => false;
        public bool IsTracingBlockHash => false;
        public bool IsTracingAccess => true;

        public AccessTxTracer(params Address[] addressesToOptimize)
        {
            _addressesToOptimize = addressesToOptimize;
        }
        
        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            throw new NotImplementedException();
        }

        public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
        {
            throw new NotImplementedException();
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            throw new NotImplementedException();
        }

        public void ReportAccountRead(Address address)
        {
            throw new NotImplementedException();
        }

        public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after)
        {
            throw new NotImplementedException();
        }

        public void ReportStorageRead(StorageCell storageCell)
        {
            throw new NotImplementedException();
        }

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            GasSpent += gasSpent;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
        {
            GasSpent += gasSpent;
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

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
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

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
            throw new NotImplementedException();
        }

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
            throw new NotImplementedException();
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        {
            throw new NotImplementedException();
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            throw new NotImplementedException();
        }

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            throw new NotImplementedException();
        }

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
            throw new NotImplementedException();
        }

        public void ReportActionError(EvmExceptionType evmExceptionType)
        {
            throw new NotImplementedException();
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            throw new NotImplementedException();
        }

        public void ReportBlockHash(Keccak blockHash)
        {
            throw new NotImplementedException();
        }

        public void ReportByteCode(byte[] byteCode)
        {
            throw new NotImplementedException();
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
            throw new NotImplementedException();
        }

        public void ReportRefund(long refund)
        {
            throw new NotImplementedException();
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            throw new NotImplementedException();
        }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
            Dictionary<Address, ISet<UInt256>> dictionary = new();
            foreach (Address address in accessedAddresses)
            {
                dictionary.Add(address, new HashSet<UInt256>());
            }

            foreach (StorageCell storageCell in accessedStorageCells)
            {
                if (!dictionary.TryGetValue(storageCell.Address, out ISet<UInt256> set))
                {
                    dictionary[storageCell.Address] = set = new HashSet<UInt256>();
                }

                set.Add(storageCell.Index);
            }

            for (int i = 0; i < _addressesToOptimize.Length; i++)
            {
                Address address = _addressesToOptimize[i];
                if (dictionary.TryGetValue(address, out ISet<UInt256> set) && set.Count <= MaxStorageAccessToOptimize)
                {
                    GasSpent += (GasCostOf.ColdSLoad - GasCostOf.WarmStateRead) * set.Count;
                    dictionary.Remove(address);
                }
            }

            AccessList = new AccessList(dictionary.ToDictionary(k => k.Key, v => (IReadOnlySet<UInt256>)v.Value));
        }
        
        public long GasSpent { get; set; }
        public AccessList? AccessList { get; private set; }
    }
}
