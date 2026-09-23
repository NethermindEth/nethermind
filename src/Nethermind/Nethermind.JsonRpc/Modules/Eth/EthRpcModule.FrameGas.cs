// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Modules.Eth;

public partial class EthRpcModule
{
    private static bool NeedsFrameGas(FrameTransactionForRpc transaction)
    {
        foreach (FrameForRpc? frame in transaction.Frames ?? [])
            if (frame is not null && (frame.ExecutionGasLimit is null || frame.StateGasLimit is null)) return true;
        return false;
    }

    private Result FillFrameGas(FrameTransactionForRpc request, BlockHeader header,
        Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null)
    {
        if (!_blockchainBridge.HasStateForBlock(header)) return Result.Fail("No state available for block");
        if (blockOverride?.GasLimit > _rpcConfig.GasCap.EffectiveGasCap()) return Result.Fail("block gas override exceeds the RPC gas cap");
        IReleaseSpec spec = _specProvider.GetSpec(header);
        Result<Transaction> converted = request.ToTransaction(validateUserInput: true, gasCap: _rpcConfig.GasCap, spec: spec);
        if (!converted.Success(out Transaction? tx, out string? error)) return Result.Fail(error);
        tx.ChainId = _blockchainBridge.GetChainId();
        if (!FrameTxValidation.IsWellFormed(tx, spec.IsEip7906Enabled, out error)) return Result.Fail(error!);

        TxFrame[] frames = tx.Frames!;
        FrameForRpc[] input = request.Frames!;
        ulong blockGas = blockOverride?.GasLimit ?? header.GasLimit;
        ulong gasCap = _rpcConfig.GasCap.EffectiveGasCap();
        BlockHeader executionHeader = header.Clone();
        executionHeader.GasUsed = 0;
        if (!request.ShouldSetBaseFee())
        {
            executionHeader.BaseFeePerGas = 0;
            if (blockOverride?.BaseFeePerGas is not null) blockOverride = blockOverride.WithBaseFee(0);
        }
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken token = timeout.Token;

        // Probe the complete transaction so earlier writes, approvals, and warm accesses are retained between frames.
        CallOutput Probe()
        {
            token.ThrowIfCancellationRequested();
            if (!FrameTxValidation.TryCalculateBlockGasReservations(tx, spec, out ulong execution, out ulong state)
                || execution > Math.Min(blockGas, Eip7825Constants.DefaultTxGasLimitCap) || state > blockGas)
                return new CallOutput { Error = "frame gas estimation exceeds the block or transaction gas limit" };
            ulong frameGas = FrameTxValidation.TotalGasLimit(frames);
            ulong signatureGas = FrameTxValidation.SignatureVerificationWorkGas(tx);
            if (frameGas > gasCap || signatureGas > gasCap - frameGas)
                return new CallOutput { Error = "frame gas estimation exceeds the RPC gas cap" };

            Transaction probe = new();
            tx.CopyTo(probe, copyHash: false);
            probe.GasLimit = frameGas;
            return _blockchainBridge.Call(executionHeader, probe, stateOverride, blockOverride: blockOverride, cancellationToken: token);
        }

        static bool Succeeded(CallOutput result) => result.Error is null && result.FailedFrameIndex is null
            && result.FrameReceipts is { Length: > 0 };

        static TxFrame WithGas(TxFrame frame, ulong execution, ulong state) =>
            new(frame.Mode, frame.Flags, frame.Target, execution, state, frame.Value, frame.Data);

        CallOutput result = Probe();
        while (!Succeeded(result))
        {
            if (result.FailedFrameIndex is not { } index)
                return Result.Fail(result.Error ?? "frame transaction simulation failed");
            TxFrame frame = frames[index];
            bool fillExecution = input[index].ExecutionGasLimit is null;
            bool fillState = input[index].StateGasLimit is null;
            if (!fillExecution && !fillState)
                return Result.Fail($"frame {index} failed with its supplied gas limits");

            FrameTxValidation.TryCalculateBlockGasReservations(tx, spec, out ulong reservedExecution, out ulong reservedState);
            ulong remainingRpc = gasCap - FrameTxValidation.TotalGasLimit(frames) - FrameTxValidation.SignatureVerificationWorkGas(tx);
            ulong executionRoom = fillExecution ? Math.Min(blockGas, Eip7825Constants.DefaultTxGasLimitCap) - reservedExecution : 0;
            ulong stateRoom = fillState ? blockGas - reservedState : 0;
            if (executionRoom > remainingRpc || stateRoom > remainingRpc - executionRoom)
            {
                executionRoom = Math.Min(executionRoom, stateRoom == 0 ? remainingRpc : remainingRpc / 2);
                stateRoom = Math.Min(stateRoom, remainingRpc - executionRoom);
            }
            ulong execution = fillExecution ? Grow(frame.ExecutionGasLimit, frame.ExecutionGasLimit + executionRoom) : frame.ExecutionGasLimit;
            ulong state = fillState ? Grow(frame.StateGasLimit, frame.StateGasLimit + stateRoom) : frame.StateGasLimit;
            if (execution == frame.ExecutionGasLimit && state == frame.StateGasLimit)
                return Result.Fail($"cannot estimate gas for frame {index}");
            frames[index] = WithGas(frame, execution, state);
            result = Probe();
        }

        for (int i = 0; i < frames.Length; i++)
        {
            if (input[i].ExecutionGasLimit is null) Minimize(i, execution: true);
            if (input[i].StateGasLimit is null) Minimize(i, execution: false);
        }

        // Re-run the final combination: a contract may inspect another frame's budget or the transaction's maximum cost.
        result = Probe();
        if (!Succeeded(result)) return Result.Fail(result.Error ?? "estimated frame gas limits failed simulation");
        request.Frames = FrameForRpc.FromFrames(frames);
        return Result.Success;

        void Minimize(int index, bool execution)
        {
            TxFrame frame = frames[index];
            ulong low = 0;
            ulong high = execution ? frame.ExecutionGasLimit : frame.StateGasLimit;
            while (low < high)
            {
                ulong middle = low + (high - low) / 2;
                frames[index] = WithGas(frame, execution ? middle : frame.ExecutionGasLimit, execution ? frame.StateGasLimit : middle);
                if (Succeeded(Probe())) high = middle;
                else low = middle + 1;
            }
            frames[index] = WithGas(frame, execution ? high : frame.ExecutionGasLimit, execution ? frame.StateGasLimit : high);
        }

        static ulong Grow(ulong value, ulong cap) => value >= cap ? cap : Math.Min(cap, value > cap / 2 ? cap : Math.Max(1_024UL, value * 2));
    }
}
