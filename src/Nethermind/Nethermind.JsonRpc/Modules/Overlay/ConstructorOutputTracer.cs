// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Overlay;

/// <summary>Captures the runtime code that the transaction at <paramref name="txIndex"/> deploys at <paramref name="address"/>,
/// or returns when it runs the creation code as a call.</summary>
internal sealed class ConstructorOutputTracer : TxTracer, IBlockTracer
{
    private readonly Address _address;
    private readonly int _targetIndex;
    private int _txIndex;

    public ConstructorOutputTracer(Address address, int txIndex)
    {
        _address = address;
        _targetIndex = txIndex;
        IsTracingReceipt = true;
        IsTracingActions = true;
    }

    public byte[]? DeployedCode { get; private set; }

    public byte[]? Output { get; private set; }

    public string? Error { get; private set; }

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block) => _txIndex = -1;

    public ITxTracer StartNewTxTrace(Transaction? tx) =>
        tx is not null && ++_txIndex == _targetIndex ? this : NullTxTracer.Instance;

    public void EndTxTrace() { }

    public void EndBlockTrace() { }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        if (deploymentAddress == _address) DeployedCode = deployedCode.ToArray();
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
        Output = output;

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) =>
        Error = error ?? "execution failed";
}
