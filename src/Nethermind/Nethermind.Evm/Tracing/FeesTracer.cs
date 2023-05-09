// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public class FeesTracer : IBlockTracer, ITxTracer
{
    public bool IsTracingRewards => false;
    public bool IsTracingState => false;
    public bool IsTracingActions => false;
    public bool IsTracingOpLevelStorage => false;
    public bool IsTracingMemory => false;
    public bool IsTracingInstructions => false;
    public bool IsTracingRefunds => false;
    public bool IsTracingCode => false;
    public bool IsTracingStack => false;
    public bool IsTracingBlockHash => false;
    public bool IsTracingAccess => false;
    public bool IsTracingStorage => false;
    public bool IsTracingReceipt => false;
    public bool IsTracingFees => true;

    public UInt256 Fees { get; private set; } = UInt256.Zero;
    public UInt256 BurntFees { get; private set; } = UInt256.Zero;

    public void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        Fees += fees;
        BurntFees += burntFees;
    }

    public void StartNewBlockTrace(Block block)
    {
        Fees = UInt256.Zero;
        BurntFees = UInt256.Zero;
    }

    public ITxTracer StartNewTxTrace(Transaction? tx) => this;

    public void EndTxTrace() { }

    public void EndBlockTrace() { }

    public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null) { }

    public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null) { }

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        throw new NotImplementedException();
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
    public void ReportStorageChange(in StorageCell storageCell, in UInt256 before, in UInt256 after)
    {
        throw new NotImplementedException();
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
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

    public void ReportStorageChange(in UInt256 key, in UInt256 value)
    {
        throw new NotImplementedException();
    }

    public void SetOperationStorage(Address address, in UInt256 storageIndex, in UInt256 newValue, in UInt256 currentValue)
    {
        throw new NotImplementedException();
    }

    public void LoadOperationStorage(Address address, in UInt256 storageIndex, in UInt256 value)
    {
        throw new NotImplementedException();
    }

    public void ReportSelfDestruct(Address address, in UInt256 balance, Address refundAddress)
    {
        throw new NotImplementedException();
    }

    public void ReportAction(long gas, in UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
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
        throw new NotImplementedException();
    }
}
