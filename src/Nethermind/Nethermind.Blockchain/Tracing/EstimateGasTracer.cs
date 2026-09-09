// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public class EstimateGasTracer : TxTracer
{
    public EstimateGasTracer() => _currentGasAndNesting.Push(new GasAndNesting(0, -1));

    public override bool IsTracingReceipt => true;
    public override bool IsTracingActions => true;
    public override bool IsTracingRefunds => true;

    public byte[]? ReturnValue { get; set; }

    private ulong NonIntrinsicGasSpentBeforeRefund { get; set; }

    internal ulong GasSpent { get; set; }

    internal ulong IntrinsicGasAt { get; set; }

    internal ulong TotalRefund { get; private set; }

    public string? Error { get; set; }

    public byte StatusCode { get; set; }

    public bool OutOfGas { get; private set; }

    public bool TopLevelRevert { get; private set; }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs,
        Hash256? stateRoot = null)
    {
        GasSpent = gasSpent.SpentGas;
        ReturnValue = output;
        StatusCode = Evm.StatusCode.Success;
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error,
        Hash256? stateRoot = null)
    {
        GasSpent = gasSpent.SpentGas;
        Error = error;
        ReturnValue = output ?? [];
        StatusCode = Evm.StatusCode.Failure;
    }

    private class GasAndNesting(ulong gasOnStart, int nestingLevel)
    {
        public ulong GasOnStart { get; set; } = gasOnStart;
        public ulong GasUsageFromChildren { get; set; }
        public ulong GasLeft { get; set; }
        public int NestingLevel { get; set; } = nestingLevel;

        // Above this the 64/63 step no longer fits in a ulong; the value is only advisory (GasEstimator.CheckFunds),
        // so saturate instead of letting the (ulong) cast throw.
        private const ulong MaxScalableGas = ulong.MaxValue / 64 * 63;

        private ulong MaxGasNeeded
        {
            get
            {
                // Saturating on purpose: the 64/63 factor is applied NestingLevel times per frame and the children's
                // figure is already scaled, so it compounds with call depth and exceeds ulong a few hundred frames
                // deep. This getter runs inside VirtualMachine.ExecuteTransaction's try block, where an
                // OverflowException is treated as an EVM frame failure (HandleFailure -> ReportActionError) and
                // unbalances the action stack ("Stack empty."). It must never throw.
                ulong maxGasNeeded = GasOnStart.SaturatingAdd(ExtraGasPressure).SaturatingSub(GasLeft).SaturatingAdd(GasUsageFromChildren);
                for (int i = 0; i < NestingLevel; i++)
                {
                    if (maxGasNeeded > MaxScalableGas)
                    {
                        return ulong.MaxValue;
                    }

                    maxGasNeeded = (ulong)Math.Ceiling(maxGasNeeded * 64m / 63);
                }

                return maxGasNeeded;
            }
        }

        public ulong AdditionalGasRequired => MaxGasNeeded.SaturatingSub(GasOnStart - GasLeft);
        public ulong ExtraGasPressure { get; set; }
    }

    internal ulong CalculateAdditionalGasRequired(Transaction tx, IReleaseSpec releaseSpec)
    {
        ulong intrinsicGas = tx.GasLimit - IntrinsicGasAt;
        // Saturating for the same reason as MaxGasNeeded: AdditionalGasRequired can legitimately be ulong.MaxValue,
        // and an unchecked add would wrap it to a small number that GasEstimator.CheckFunds then reports as a
        // successful estimate.
        return _currentGasAndNesting.Peek().AdditionalGasRequired
            .SaturatingAdd(RefundHelper.CalculateClaimableRefund(intrinsicGas + NonIntrinsicGasSpentBeforeRefund, TotalRefund, releaseSpec));
    }

    private int _currentNestingLevel = -1;

    private bool _isInPrecompile;

    private readonly Stack<GasAndNesting> _currentGasAndNesting = new();

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
        ExecutionType callType, bool isPrecompileCall = false)
    {
        if (_currentNestingLevel == -1)
        {
            OutOfGas = false;
            TopLevelRevert = false;
            IntrinsicGasAt = gas;
        }

        if (!isPrecompileCall)
        {
            _currentNestingLevel++;
            _currentGasAndNesting.Push(new GasAndNesting(gas, _currentNestingLevel));
        }
        else
        {
            _isInPrecompile = true;
        }
    }

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) => UpdateAdditionalGas(gas);

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) =>
        UpdateAdditionalGas(gas);

    public override void ReportActionError(EvmExceptionType exceptionType) => HandleActionError(exceptionType);

    public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output) => HandleActionError(EvmExceptionType.Revert);

    private void HandleActionError(EvmExceptionType exceptionType)
    {
        ReportOperationError(exceptionType);
        UpdateAdditionalGas();
    }

    public void ReportActionError(EvmExceptionType exceptionType, ulong gasLeft)
    {
        ReportOperationError(exceptionType);
        UpdateAdditionalGas(gasLeft);
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        if (_currentNestingLevel == 0)
        {
            OutOfGas |= error == EvmExceptionType.OutOfGas;

            if (error == EvmExceptionType.Revert)
            {
                TopLevelRevert = true;
            }
        }
    }

    private void UpdateAdditionalGas(ulong? gasLeft = null)
    {
        if (_isInPrecompile)
        {
            _isInPrecompile = false;
        }
        else
        {
            GasAndNesting current = _currentGasAndNesting.Pop();

            if (gasLeft.HasValue)
            {
                current.GasLeft = gasLeft.Value;
            }

            GasAndNesting parent = _currentGasAndNesting.Peek();
            parent.GasUsageFromChildren = parent.GasUsageFromChildren.SaturatingAdd(current.AdditionalGasRequired);
            _currentNestingLevel--;

            if (_currentNestingLevel == -1)
            {
                NonIntrinsicGasSpentBeforeRefund = IntrinsicGasAt - current.GasLeft;
            }
        }
    }

    public override void ReportRefund(long refund) => TotalRefund += (ulong)refund;

    public override void ReportExtraGasPressure(ulong extraGasPressure) =>
        _currentGasAndNesting.Peek().ExtraGasPressure =
            Math.Max(_currentGasAndNesting.Peek().ExtraGasPressure, extraGasPressure);
}
