// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Modules.Eth;

public partial class EthRpcModule
{
    /// <summary>Runs <paramref name="executor"/> on the request, first filling any omitted frame gas limits.</summary>
    /// <remarks>An omitted limit is resolved only when its frame can succeed, so a frame that always reverts fails the
    /// call with <see cref="ErrorCodes.ExecutionReverted"/> instead of returning a result as explicit limits would.</remarks>
    private ResultWrapper<TResult> ExecuteWithFrameGas<TResult>(TxExecutor<TResult> executor, SignableTransactionForRpc request,
        BlockParameter? blockParameter, Dictionary<Address, AccountOverride>? stateOverride, BlockOverride? blockOverride = null)
    {
        // Requests the executor rejects before execution get its errors, as they would with explicit limits.
        if (request is not FrameTransactionForRpc frameTx || !NeedsFrameGas(frameTx) || blockOverride?.GasLimit > _rpcConfig.GasCap!.Value)
            return executor.ExecuteTx(request, blockParameter, stateOverride, blockOverride);

        SearchResult<BlockHeader> search = _blockFinder.SearchForHeader(blockParameter);
        if (search.IsError) return ResultWrapper<TResult>.Fail(search);
        if (!_blockchainBridge.HasStateForBlock(search.Object!))
            return executor.ExecuteTx(request, blockParameter, stateOverride, blockOverride, searchResult: search);
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        Result<FrameForRpc[]> result = FillFrameGas(frameTx, search.Object!, timeout.Token, out int errorCode, stateOverride, blockOverride);
        if (!result) return ResultWrapper<TResult>.Fail(result.Error!, errorCode);

        FrameForRpc[]? originalFrames = frameTx.Frames;
        frameTx.Frames = result.Data;
        try
        {
            return executor.ExecuteTx(request, blockParameter, stateOverride, blockOverride, timeout.Token, search);
        }
        finally
        {
            frameTx.Frames = originalFrames;
        }
    }

    private static bool NeedsFrameGas(FrameTransactionForRpc transaction)
    {
        foreach (FrameForRpc? frame in transaction.Frames ?? [])
            if (frame is not null && (frame.ExecutionGas is null || frame.StateGas is null)) return true;
        return false;
    }

    /// <returns>The filled frames, or the failure with <see cref="ErrorCodes.ExecutionReverted"/> when a frame reverted.</returns>
    private Result<FrameForRpc[]> FillFrameGas(FrameTransactionForRpc request, BlockHeader header, CancellationToken token, out int errorCode,
        Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null)
    {
        errorCode = ErrorCodes.InvalidInput;
        if (!_blockchainBridge.HasStateForBlock(header)) return Result<FrameForRpc[]>.Fail("No state available for block");
        IReleaseSpec spec = _specProvider.GetSpec(header);
        Result<Transaction> converted = request.ToTransaction(validateUserInput: true, gasCap: _rpcConfig.GasCap, spec: spec);
        if (!converted.Success(out Transaction? tx, out string? error)) return Result<FrameForRpc[]>.Fail(error);
        tx.ChainId = _blockchainBridge.GetChainId();
        if (!FrameTxValidation.IsWellFormed(tx, spec.IsEip7906Enabled, out error)) return Result<FrameForRpc[]>.Fail(error!);

        FrameForRpc[] input = request.Frames!;
        bool[] fillExecution = new bool[input.Length];
        bool[] fillState = new bool[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            fillExecution[i] = input[i].ExecutionGas is null;
            fillState[i] = input[i].StateGas is null;
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
