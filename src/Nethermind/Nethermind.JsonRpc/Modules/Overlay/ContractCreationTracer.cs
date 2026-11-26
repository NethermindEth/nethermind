// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Overlay;

/// <summary>Finds the transaction of a block that deployed the address and the frame that deployed it.</summary>
internal sealed class ContractCreationTracer : TxTracer, IBlockTracer
{
    private readonly Address _address;
    private int _txIndex;
    private int _actionCount;
    private bool _pendingIsTopLevel;
    private Address? _pendingCreator;
    private UInt256 _pendingValue;

    public ContractCreationTracer(Address address)
    {
        _address = address;
        IsTracingActions = true;
    }

    public int TxIndex { get; private set; } = -1;

    /// <summary>Whether the transaction itself deployed the contract, not a CREATE or CREATE2 in it.</summary>
    public bool IsTopLevel { get; private set; }

    public Address? Creator { get; private set; }

    public UInt256 Value { get; private set; }

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block) => _txIndex = -1;

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (tx is null) return NullTxTracer.Instance;

        _txIndex++;
        _actionCount = 0;
        _pendingCreator = null;
        return this;
    }

    public void EndTxTrace() { }

    public void EndBlockTrace() { }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        bool isTopLevel = _actionCount++ == 0;
        if (callType.IsAnyCreate() && to == _address)
        {
            _pendingCreator = from;
            _pendingValue = value;
            _pendingIsTopLevel = isTopLevel;
        }
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        if (deploymentAddress != _address || _pendingCreator is null) return;

        TxIndex = _txIndex;
        IsTopLevel = _pendingIsTopLevel;
        Creator = _pendingCreator;
        Value = _pendingValue;
    }
}
