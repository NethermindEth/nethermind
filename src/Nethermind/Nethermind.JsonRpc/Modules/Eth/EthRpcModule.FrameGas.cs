// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
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

    /// <returns>The filled frames, or the failure with <see cref="ErrorCodes.ExecutionReverted"/> when a frame reverted.</returns>
    private Result<FrameForRpc[]> FillFrameGas(FrameTransactionForRpc request, BlockHeader header, CancellationToken token, out int errorCode,
        Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null)
    {
        errorCode = ErrorCodes.InvalidInput;
        if (!_blockchainBridge.HasStateForBlock(header)) return Result<FrameForRpc[]>.Fail("No state available for block");
        if (blockOverride?.GasLimit > _rpcConfig.GasCap.EffectiveGasCap()) return Result<FrameForRpc[]>.Fail("block gas override exceeds the RPC gas cap");
        // The next block's rules, which the estimate runs under.
        IReleaseSpec spec = _specProvider.GetSpec(header.Number + 1, header.Timestamp + _secondsPerSlot);
        Result<Transaction> converted = request.ToTransaction(validateUserInput: true, gasCap: _rpcConfig.GasCap, spec: spec);
        if (!converted.Success(out Transaction? tx, out string? error)) return Result<FrameForRpc[]>.Fail(error);
        tx.ChainId = _blockchainBridge.GetChainId();
        if (!FrameTxValidation.IsWellFormed(tx, spec.IsEip7906Enabled, out error)) return Result<FrameForRpc[]>.Fail(error!);

        FrameForRpc[] input = request.Frames!;
        bool[] fillExecution = new bool[input.Length];
        bool[] fillState = new bool[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            fillExecution[i] = input[i].ExecutionGasLimit is null;
            fillState[i] = input[i].StateGasLimit is null;
        }
        BlockHeader executionHeader = header.Clone();
        if (!request.ShouldSetBaseFee())
        {
            executionHeader.BaseFeePerGas = 0;
            if (blockOverride?.BaseFeePerGas is not null) blockOverride = blockOverride.WithBaseFee(0);
        }
        Result<TxFrame[]> result = _blockchainBridge.EstimateFrameGas(executionHeader, tx, fillExecution, fillState,
            _rpcConfig.GasCap.EffectiveGasCap(), _rpcConfig.EstimateErrorMargin, stateOverride, blockOverride, token, out bool executionReverted);
        if (executionReverted) errorCode = ErrorCodes.ExecutionReverted;
        if (!result.Success(out TxFrame[]? frames, out error)) return Result<FrameForRpc[]>.Fail(error!);
        return FrameForRpc.FromFrames(frames);
    }
}
