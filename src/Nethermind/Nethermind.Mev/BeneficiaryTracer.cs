// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Mev
{
    public class BeneficiaryTracer : IBlockTracer, ITxTracer
    {
        private Address _beneficiary = Address.Zero;
        public UInt256 BeneficiaryBalance { get; private set; }
        public bool IsTracingState => true;
        public bool IsTracingRewards => true;
        public void StartNewBlockTrace(Block block)
        {
            _beneficiary = block.Header.GasBeneficiary!;
        }

        public ITxTracer StartNewTxTrace(Transaction? tx) => this;

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            if (address == _beneficiary)
            {
                BeneficiaryBalance = after ?? UInt256.Zero;
            }
        }

        public void EndTxTrace() { }
        public void EndBlockTrace() { }
        public bool IsTracingStorage => false;
        public bool IsTracingReceipt => false;
        public bool IsTracingActions => false;
        public bool IsTracingOpLevelStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingRefunds => false;
        public bool IsTracingCode => false;
        public bool IsTracingStack => false;
        public bool IsTracingBlockHash => false;
        public bool IsTracingAccess => false;
        public bool IsTracingFees => false;
        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }
        public void ReportCodeChange(Address address, byte[]? before, byte[]? after) { }
        public void ReportNonceChange(Address address, UInt256? before, UInt256? after) { }
        public void ReportAccountRead(Address address) { }
        public void ReportStorageChange(in StorageCell storageCell, in UInt256 before, in UInt256 after) { }
        public void ReportStorageRead(in StorageCell storageCell) { }
        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null) { }
        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null) { }
        public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false) { }
        public void ReportOperationError(EvmExceptionType error) { }
        public void ReportOperationRemainingGas(long gas) { }
        public void SetOperationStack(List<string> stackTrace) { }
        public void ReportStackPush(in ReadOnlySpan<byte> stackItem) { }
        public void SetOperationMemory(List<string> memoryTrace) { }
        public void SetOperationMemorySize(ulong newSize) { }
        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data) { }
        public void ReportStorageChange(in UInt256 key, in UInt256 value) { }
        public void SetOperationStorage(Address address, in UInt256 storageIndex, in UInt256 newValue, in UInt256 currentValue) { }
        public void LoadOperationStorage(Address address, in UInt256 storageIndex, in UInt256 value) { }
        public void ReportSelfDestruct(Address address, in UInt256 balance, Address refundAddress) { }
        public void ReportAction(long gas, in UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) { }
        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output) { }
        public void ReportActionError(EvmExceptionType evmExceptionType) { }
        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) { }
        public void ReportBlockHash(Keccak blockHash) { }
        public void ReportByteCode(byte[] byteCode) { }
        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable) { }
        public void ReportRefund(long refund) { }
        public void ReportExtraGasPressure(long extraGasPressure) { }
        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells) { }
        public void ReportFees(UInt256 fees, UInt256 burntFees) { }
    }
}
