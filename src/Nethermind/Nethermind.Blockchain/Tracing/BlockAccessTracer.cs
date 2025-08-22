// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public class BlockAccessTracer : IBlockTracer, ITxTracer, IJournal<int>
{
    // private IBlockTracer _otherTracer = NullBlockTracer.Instance;
    protected Block Block = null!;
    public bool IsTracingReceipt => false;
    public bool IsTracingActions => false;
    public bool IsTracingOpLevelStorage => false;
    public bool IsTracingMemory => false;
    public bool IsTracingInstructions => false;
    public bool IsTracingRefunds => false;
    public bool IsTracingCode => false;
    public bool IsTracingStack => false;
    public bool IsTracingState => true;
    public bool IsTracingStorage => true;

    public bool IsTracingBlockHash => false;
    public bool IsTracingAccess => true;
    public bool IsTracingFees => false;
    public bool IsTracingLogs => false;

    public void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        // _txReceipts.Add(BuildReceipt(recipient, gasSpent.SpentGas, StatusCode.Success, logs, stateRoot));
    }

    public void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        // _txReceipts.Add(BuildFailedReceipt(recipient, gasSpent.SpentGas, error, stateRoot));

    }

    public void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0) { }

    public void ReportOperationError(EvmExceptionType error) { }


    public void ReportOperationRemainingGas(long gas) { }

    public void ReportLog(LogEntry log) { }

    public void SetOperationMemorySize(ulong newSize) { }

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data) { }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        if (!_bal.AccountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _bal.AccountChanges.Add(address, accountChanges);
        }

        if (currentValue != newValue)
        {
            StorageChange(accountChanges, new StorageCell(address, storageIndex).Hash.BytesAsSpan, newValue);
        }
    }

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value) { }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) { }

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        BalanceChange balanceChange = new()
        {
            BlockAccessIndex = _blockAccessIndex,
            PostBalance = after!.Value
        };

        if (!_bal.AccountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _bal.AccountChanges.Add(address, accountChanges);
        }

        // don't add zero balance transfers, but add empty account changes
        if ((before ?? 0) == after)
        {
            return;
        }

        List<BalanceChange> balanceChanges = accountChanges.BalanceChanges;
        if (balanceChanges is not [] && balanceChanges[^1].BlockAccessIndex == _blockAccessIndex)
        {
            balanceChanges.RemoveAt(balanceChanges.Count - 1);
        }
        balanceChanges.Add(balanceChange);
    }

    public void ReportCodeChange(Address address, byte[] before, byte[] after)
    {
        CodeChange codeChange = new()
        {
            BlockAccessIndex = _blockAccessIndex,
            NewCode = after
        };

        if (!_bal.AccountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _bal.AccountChanges.Add(address, accountChanges);
        }

        List<CodeChange> codeChanges = accountChanges.CodeChanges;
        if (codeChanges is not [] && codeChanges[^1].BlockAccessIndex == _blockAccessIndex)
        {
            codeChanges.RemoveAt(codeChanges.Count - 1);
        }
        codeChanges.Add(codeChange);
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        NonceChange nonceChange = new()
        {
            BlockAccessIndex = _blockAccessIndex,
            NewNonce = (ulong)after
        };

        if (!_bal.AccountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _bal.AccountChanges.Add(address, accountChanges);
        }

        List<NonceChange> nonceChanges = accountChanges.NonceChanges;
        if (nonceChanges is not [] && nonceChanges[^1].BlockAccessIndex == _blockAccessIndex)
        {
            nonceChanges.RemoveAt(nonceChanges.Count - 1);
        }
        nonceChanges.Add(nonceChange);
    }

    public void ReportAccountRead(Address address)
    {
        if (!_bal.AccountChanges.ContainsKey(address))
        {
            _bal.AccountChanges.Add(address, new(address));
        }
    }

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        // no address
    }

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        Address address = storageCell.Address;

        if (!_bal.AccountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _bal.AccountChanges.Add(address, accountChanges);
        }

        if (!Enumerable.SequenceEqual(before, after))
        {
            StorageChange(accountChanges, storageCell.Hash.BytesAsSpan, after.AsSpan());
        }
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        StorageKey storageKey = new()
        {
            Key = storageCell.Hash.ToByteArray()
        };
        Address address = Address.Zero;

        if (!_bal.AccountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _bal.AccountChanges.Add(address, accountChanges);
        }

        accountChanges.StorageReads.Add(storageKey);
    }

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) { }

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output) { }

    public void ReportActionError(EvmExceptionType exceptionType) { }

    public void ReportActionRevert(long gasLeft, ReadOnlyMemory<byte> output) { }

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) { }

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode) { }

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable) { }

    public void ReportRefund(long refund) { }

    public void ReportExtraGasPressure(long extraGasPressure) { }

    public void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells)
    {
        // _bal.Add(new());
    }

    public void SetOperationStack(TraceStack stack) { }

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem) { }

    public void ReportBlockHash(Hash256 blockHash) { }

    public void SetOperationMemory(TraceMemory memoryTrace) { }

    public void ReportFees(UInt256 fees, UInt256 burntFees) { }

    // private ITxTracer _currentTxTracer = NullTxTracer.Instance;
    private ushort _blockAccessIndex = 0;
    // private readonly List<TxReceipt> _txReceipts = new();
    private BlockAccessList _bal = new();
    protected Transaction? CurrentTx;
    public BlockAccessList BlockAccessList => _bal;
    // public TxReceipt LastReceipt => _txReceipts[^1];
    public bool IsTracingRewards => false;

    // public ITxTracer InnerTracer => _currentTxTracer;

    public int TakeSnapshot() => _bal.AccountChanges.Count;

    public void Restore(int snapshot)
    {
        // int numToRemove = _txReceipts.Count - snapshot;

        // for (int i = 0; i < numToRemove; i++)
        // {
        //     _txReceipts.RemoveAt(_txReceipts.Count - 1);
        // }

        // Block.Header.GasUsed = _txReceipts.Count > 0 ? _txReceipts.Last().GasUsedTotal : 0;
    }

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block)
    {
        Block = block;
        _blockAccessIndex = 0;
        _bal = new();
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        return this;
    }

    public void EndTxTrace()
    {
        _blockAccessIndex++;
    }

    public void EndBlockTrace()
    {
        // _otherTracer.EndBlockTrace();
        // if (_txReceipts.Count > 0)
        // {
        //     Bloom blockBloom = new();
        //     Block.Header.Bloom = blockBloom;
        //     for (int index = 0; index < _txReceipts.Count; index++)
        //     {
        //         TxReceipt? receipt = _txReceipts[index];
        //         blockBloom.Accumulate(receipt.Bloom!);
        //     }
        // }
    }

    // public void SetOtherTracer(IBlockTracer blockTracer)
    // {
    //     _otherTracer = blockTracer;
    // }

    public void Dispose()
    {
        // _currentTxTracer.Dispose();
    }

    private void StorageChange(AccountChanges accountChanges, in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        StorageChange storageChange = new()
        {
            BlockAccessIndex = _blockAccessIndex,
            NewValue = value.ToArray()
        };

        StorageKey storageKey = new(key);
        if (!accountChanges.StorageChanges.TryGetValue(storageKey, out SlotChanges storageChanges))
        {
            storageChanges = new();
        }
        else if (storageChanges.Changes is not [] && storageChanges.Changes[^1].BlockAccessIndex == _blockAccessIndex)
        {
            storageChanges.Changes.RemoveAt(storageChanges.Changes.Count - 1);
        }
        storageChanges.Changes.Add(storageChange);
    }

}
