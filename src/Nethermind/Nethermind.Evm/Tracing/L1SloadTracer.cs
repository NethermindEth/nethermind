// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

/// <summary>
/// Tracer for detecting L1SLOAD precompile calls.
/// </summary>
public class L1SloadTracer : TxTracer, IBlockTracer
{
    private readonly List<L1SloadCall> _calls = [];
    private readonly Address _l1SloadAddress = Address.FromNumber(0x12);

    public override bool IsTracingActions => true;
    public override bool IsTracingReceipt => true;
    public IReadOnlyList<L1SloadCall> Calls => _calls;
    public bool HasCalls => _calls.Count > 0;
    public int CallCount => _calls.Count;
    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block)
    {
        _calls.Clear();
    }

    public ITxTracer StartNewTxTrace(Transaction? tx) => this;

    public void EndTxTrace() { }

    public void EndBlockTrace() { }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to,
        ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        if (isPrecompileCall && to == _l1SloadAddress)
        {
            L1SloadCall? call = ParseL1SloadCall(from, input, gas);
            if (call != null)
            {
                _calls.Add(call);
            }
        }
    }

    public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output,
        LogEntry[] logs, Hash256? stateRoot = null)
    {
        if (_calls.Count > 0)
        {
            _calls[^1].Success = true;
            _calls[^1].Output = output;
            _calls[^1].GasUsed = gasSpent.SpentGas;
        }
    }

    public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output,
        string? error, Hash256? stateRoot = null)
    {
        if (_calls.Count > 0)
        {
            _calls[^1].Success = false;
            _calls[^1].Error = error;
            _calls[^1].GasUsed = gasSpent.SpentGas;
        }
    }

    private L1SloadCall? ParseL1SloadCall(Address from, ReadOnlyMemory<byte> input, long gas)
    {
        try
        {
            if (input.Length != L1PrecompileConstants.ExpectedInputLength)
            {
                return new L1SloadCall
                {
                    From = from,
                    Gas = gas,
                    Success = false,
                    Error = $"Invalid input length: expected {L1PrecompileConstants.ExpectedInputLength}, got {input.Length}"
                };
            }

            ReadOnlySpan<byte> inputSpan = input.Span;

            var l1ContractAddress = new Address(inputSpan[..L1PrecompileConstants.AddressBytes]);
            var storageKey = new UInt256(inputSpan[L1PrecompileConstants.AddressBytes..(L1PrecompileConstants.AddressBytes + L1PrecompileConstants.StorageKeyBytes)], isBigEndian: true);
            var blockNumber = new UInt256(inputSpan[(L1PrecompileConstants.AddressBytes + L1PrecompileConstants.StorageKeyBytes)..], isBigEndian: true);

            return new L1SloadCall
            {
                From = from,
                Gas = gas,
                L1ContractAddress = l1ContractAddress,
                StorageKey = storageKey,
                BlockNumber = blockNumber
            };
        }
        catch (Exception ex)
        {
            return new L1SloadCall
            {
                From = from,
                Gas = gas,
                Success = false,
                Error = $"Failed to parse input: {ex.Message}"
            };
        }
    }

    public void Clear() => _calls.Clear();
}

/// <summary>
/// Represents a single L1SLOAD precompile call
/// </summary>
public class L1SloadCall
{
    public Address From { get; set; }
    public long Gas { get; set; }
    public Address? L1ContractAddress { get; set; }
    public UInt256? StorageKey { get; set; }
    public UInt256? BlockNumber { get; set; }
    public bool? Success { get; set; }
    public byte[]? Output { get; set; }
    public long? GasUsed { get; set; }
    public string? Error { get; set; }
}
