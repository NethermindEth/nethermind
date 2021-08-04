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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public class BlockReceiptsTracer : IBlockTracer, ITxTracer
    {
        private Block _block = null!;
        public bool IsTracingReceipt => true;
        public bool IsTracingActions => _currentTxTracer.IsTracingActions;
        public bool IsTracingOpLevelStorage => _currentTxTracer.IsTracingOpLevelStorage;
        public bool IsTracingMemory => _currentTxTracer.IsTracingMemory;
        public bool IsTracingInstructions => _currentTxTracer.IsTracingInstructions;
        public bool IsTracingRefunds => _currentTxTracer.IsTracingRefunds;
        public bool IsTracingCode => _currentTxTracer.IsTracingCode;
        public bool IsTracingStack => _currentTxTracer.IsTracingStack;
        public bool IsTracingState => _currentTxTracer.IsTracingState;
        public bool IsTracingStorage => _currentTxTracer.IsTracingStorage;
        
        public bool IsTracingBlockHash => _currentTxTracer.IsTracingBlockHash;
        public bool IsTracingAccess => _currentTxTracer.IsTracingAccess;

        private IBlockTracer _otherTracer = NullBlockTracer.Instance;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
        {
            _txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));
            
            // hacky way to support nested receipt tracers
            if (_otherTracer is ITxTracer otherTxTracer)
            {
                otherTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
            }
            
            if (_currentTxTracer.IsTracingReceipt)
            {
                _currentTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs);
            }
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
        {
            _txReceipts.Add(BuildFailedReceipt(recipient, gasSpent, error, stateRoot));
            
            // hacky way to support nested receipt tracers
            if (_otherTracer is ITxTracer otherTxTracer)
            {
                otherTxTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
            }
            
            if (_currentTxTracer.IsTracingReceipt)
            {
                _currentTxTracer.MarkAsFailed(recipient, gasSpent, output, error);
            }
        }

        private TxReceipt BuildFailedReceipt(Address recipient, long gasSpent, string error, Keccak stateRoot = null)
        {
            TxReceipt receipt = BuildReceipt(recipient, gasSpent, StatusCode.Failure, Array.Empty<LogEntry>(), stateRoot);
            receipt.Error = error;
            return receipt;
        }

        private TxReceipt BuildReceipt(Address recipient, long spentGas, byte statusCode, LogEntry[] logEntries, Keccak stateRoot = null)
        {
            Transaction transaction = _currentTx;
            TxReceipt txReceipt = new()
            {
                Logs = logEntries,
                TxType = transaction.Type,
                Bloom = logEntries.Length == 0 ? Bloom.Empty : new Bloom(logEntries),
                GasUsedTotal = _block.GasUsed,
                StatusCode = statusCode,
                Recipient = transaction.IsContractCreation ? null : recipient,
                BlockHash = _block.Hash,
                BlockNumber = _block.Number,
                Index = _currentIndex,
                GasUsed = spentGas,
                Sender = transaction.SenderAddress,
                ContractAddress = transaction.IsContractCreation ? recipient : null,
                TxHash = transaction.Hash,
                PostTransactionState = stateRoot
            };

            return txReceipt;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc) =>
            _currentTxTracer.StartOperation(depth, gas, opcode, pc);

        public void ReportOperationError(EvmExceptionType error) =>
            _currentTxTracer.ReportOperationError(error);
        

        public void ReportOperationRemainingGas(long gas) =>
            _currentTxTracer.ReportOperationRemainingGas(gas);
        

        public void SetOperationMemorySize(ulong newSize) =>
            _currentTxTracer.SetOperationMemorySize(newSize);
        
        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data) =>
            _currentTxTracer.ReportMemoryChange(offset, data);
        
        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) =>
            _currentTxTracer.ReportStorageChange(key, value);
        
        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) =>
            _currentTxTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) =>
            _currentTxTracer.ReportSelfDestruct(address, balance, refundAddress);
        
        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) =>
            _currentTxTracer.ReportBalanceChange(address, before, after);
        
        public void ReportCodeChange(Address address, byte[] before, byte[] after) =>
            _currentTxTracer.ReportCodeChange(address, before, after);

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after) =>
            _currentTxTracer.ReportNonceChange(address, before, after);
        
        public void ReportAccountRead(Address address) =>
            _currentTxTracer.ReportAccountRead(address);
        
        public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after) =>
            _currentTxTracer.ReportStorageChange(storageCell, before, after);
        
        public void ReportStorageRead(StorageCell storageCell) =>
            _currentTxTracer.ReportStorageRead(storageCell);
        
        public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) =>
            _currentTxTracer.ReportAction(gas, value, @from, to, input, callType, isPrecompileCall);
        
        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output) =>
            _currentTxTracer.ReportActionEnd(gas, output);
        
        public void ReportActionError(EvmExceptionType exceptionType) =>
            _currentTxTracer.ReportActionError(exceptionType);
        
        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) =>
            _currentTxTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);
        
        public void ReportByteCode(byte[] byteCode) =>
            _currentTxTracer.ReportByteCode(byteCode);
        
        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable) =>
            _currentTxTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);
        
        public void ReportRefund(long refund) =>
            _currentTxTracer.ReportRefund(refund);

        public void ReportExtraGasPressure(long extraGasPressure) =>
            _currentTxTracer.ReportExtraGasPressure(extraGasPressure);
        
        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells) =>
            _currentTxTracer.ReportAccess(accessedAddresses, accessedStorageCells);
        
        public void SetOperationStack(List<string> stackTrace) =>
            _currentTxTracer.SetOperationStack(stackTrace);
        
        public void ReportStackPush(in ReadOnlySpan<byte> stackItem) =>
            _currentTxTracer.ReportStackPush(stackItem);
        
        public void ReportBlockHash(Keccak blockHash) =>
            _currentTxTracer.ReportBlockHash(blockHash);
        
        public void SetOperationMemory(List<string> memoryTrace) =>
            _currentTxTracer.SetOperationMemory(memoryTrace);
        
        private ITxTracer _currentTxTracer = NullTxTracer.Instance;
        private int _currentIndex;
        private readonly List<TxReceipt> _txReceipts = new();
        private Transaction? _currentTx;
        public IReadOnlyList<TxReceipt> TxReceipts => _txReceipts;
        public TxReceipt LastReceipt => _txReceipts[^1];
        public bool IsTracingRewards => _otherTracer.IsTracingRewards;
        public int TakeSnapshot() => _txReceipts.Count;
        
        public void RestoreSnapshot(int length)
        {
            int numToRemove = _txReceipts.Count - length;
            
            for (int i = 0; i < numToRemove; i++)
            {
                _txReceipts.RemoveAt(_txReceipts.Count - 1);
            }
            
            _block.Header.GasUsed = _txReceipts.Count > 0 ? _txReceipts.Last().GasUsedTotal : 0;
        }

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
            _otherTracer.ReportReward(author, rewardType, rewardValue);
        
        public void StartNewBlockTrace(Block block)
        {
            if (_otherTracer is null)
            {
                throw new InvalidOperationException("other tracer not set in receipts tracer");
            }
            
            _block = block;
            _currentIndex = 0;
            _txReceipts.Clear();

            _otherTracer.StartNewBlockTrace(block);
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            _currentTx = tx;
            _currentTxTracer = _otherTracer.StartNewTxTrace(tx);
            return _currentTxTracer;
        }

        public void EndTxTrace()
        {
            _otherTracer.EndTxTrace();
            _currentIndex++;
        }
        
        public void EndBlockTrace()
        {
            _otherTracer.EndBlockTrace();
            if (_txReceipts.Count > 0)
            {
                Bloom blockBloom = new();
                _block.Header.Bloom = blockBloom;
                for (var index = 0; index < _txReceipts.Count; index++)
                {
                    var receipt = _txReceipts[index];
                    blockBloom.Accumulate(receipt.Bloom!);
                }
            }
        }

        public void SetOtherTracer(IBlockTracer blockTracer)
        {
            _otherTracer = blockTracer;
        }
    }
}
