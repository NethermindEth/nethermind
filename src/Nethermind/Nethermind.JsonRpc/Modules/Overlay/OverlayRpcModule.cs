// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Filters;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.JsonRpc.Modules.Overlay;

public class OverlayRpcModule(
    IOverridableEnv<ITracer> tracerEnv,
    IBlockFinder blockFinder,
    IStateReader stateReader,
    FilterStore filterStore,
    ISpecProvider specProvider,
    IJsonRpcConfig jsonRpcConfig) : IOverlayRpcModule
{
    public ResultWrapper<OverlayCreationCode?> overlay_callConstructor(Address address, byte[] code)
    {
        using CancellationTokenSource timeout = jsonRpcConfig.BuildTimeoutCancellationToken();
        CancellationToken token = timeout.Token;

        BlockHeader? head = blockFinder.Head?.Header;
        if (head is null) return ResultWrapper<OverlayCreationCode?>.Fail("head block not found", ErrorCodes.ResourceNotFound);
        if (!stateReader.HasStateForBlock(head)) return NoState<OverlayCreationCode?>(head);
        if (!HasCode(head, address)) return ResultWrapper<OverlayCreationCode?>.Fail($"no contract at {address}", ErrorCodes.ResourceNotFound);

        ulong low = 0, high = head.Number;
        while (low < high)
        {
            token.ThrowIfCancellationRequested();
            ulong middle = low + (high - low) / 2;
            BlockHeader? header = blockFinder.FindHeader(middle, BlockTreeLookupOptions.RequireCanonical);
            if (header is null) return ResultWrapper<OverlayCreationCode?>.Fail($"block {middle} not found", ErrorCodes.ResourceNotFound);
            if (!stateReader.HasStateForBlock(header)) return NoState<OverlayCreationCode?>(header);

            if (HasCode(header, address)) high = middle;
            else low = middle + 1;
        }

        Block? block = low == 0 ? null : blockFinder.FindBlock(low, BlockTreeLookupOptions.RequireCanonical);
        if (block is null) return ResultWrapper<OverlayCreationCode?>.Fail("contract construction txn not found", ErrorCodes.ResourceNotFound);

        try
        {
            ContractCreationTracer creation = new(address);
            if (!TryTrace(new Block(block.Header.Clone(), block.Body), null, creation, token, out ResultWrapper<OverlayCreationCode?>? failure)) return failure;
            if (creation.TxIndex < 0) return ResultWrapper<OverlayCreationCode?>.Fail("contract construction txn not found", ErrorCodes.ResourceNotFound);

            ulong gasLimit = jsonRpcConfig.GasCap ?? block.Header.GasLimit;
            Transaction[] transactions = new Transaction[creation.TxIndex + 1];
            Array.Copy(block.Transactions, transactions, creation.TxIndex);
            Dictionary<Address, AccountOverride>? stateOverride = null;
            if (creation.IsTopLevel)
            {
                Transaction deployment = new();
                block.Transactions[creation.TxIndex].CopyTo(deployment, copyHash: true);
                deployment.Data = code;
                deployment.GasLimit = gasLimit;
                deployment.GasPrice = 0;
                deployment.DecodedMaxFeePerGas = 0;
                transactions[^1] = deployment;
            }
            else
            {
                // A CREATE or CREATE2 in a contract cannot take other code, so the code runs as a call to its address.
                stateOverride = new() { [address] = new AccountOverride { Code = code } };
                Transaction call = new()
                {
                    SenderAddress = creation.Creator,
                    To = address,
                    Value = creation.Value,
                    GasLimit = gasLimit,
                };
                call.Hash = call.CalculateHash();
                transactions[^1] = call;
            }

            Block replay = new(block.Header.Clone(), new BlockBody(transactions, block.Uncles, block.Withdrawals));
            ConstructorOutputTracer output = new(address, creation.TxIndex);
            if (!TryTrace(replay, stateOverride, output, token, out failure)) return failure;
            if (output.Error is not null) return ResultWrapper<OverlayCreationCode?>.Fail(output.Error, ErrorCodes.ExecutionError);

            byte[]? runtimeCode = creation.IsTopLevel ? output.DeployedCode : output.Output;
            return ResultWrapper<OverlayCreationCode?>.Success(runtimeCode is { Length: > 0 } ? new OverlayCreationCode(runtimeCode) : null);
        }
        catch (InvalidBlockException e)
        {
            return ResultWrapper<OverlayCreationCode?>.Fail(e.Message, ErrorCodes.ExecutionError);
        }
    }

    public ResultWrapper<IEnumerable<FilterLog>> overlay_getLogs(Filter filter, Dictionary<Address, AccountOverride>? stateOverride = null)
    {
        using CancellationTokenSource timeout = jsonRpcConfig.BuildTimeoutCancellationToken();
        CancellationToken token = timeout.Token;

        if (blockFinder.IsRangeInFuture(filter.FromBlock, filter.ToBlock))
            return ResultWrapper<IEnumerable<FilterLog>>.Fail(BlockFinderExtensions.BlockRangeInFuture, ErrorCodes.InvalidParams);

        SearchResult<BlockHeader> fromResult = blockFinder.SearchForHeader(filter.FromBlock);
        if (fromResult.IsError) return ResultWrapper<IEnumerable<FilterLog>>.Fail(fromResult);
        SearchResult<BlockHeader> toResult = blockFinder.SearchForHeader(filter.ToBlock);
        if (toResult.IsError) return ResultWrapper<IEnumerable<FilterLog>>.Fail(toResult);

        ulong from = fromResult.Object!.Number;
        ulong to = toResult.Object!.Number;
        if (from > to) return ResultWrapper<IEnumerable<FilterLog>>.Fail("invalid block range params", ErrorCodes.InvalidParams);

        int maxRange = jsonRpcConfig.OverlayMaxBlockRange;
        if (maxRange > 0 && to - from >= (ulong)maxRange)
            return ResultWrapper<IEnumerable<FilterLog>>.Fail($"exceed maximum block range: {maxRange}", ErrorCodes.LimitExceeded);

        LogFilter logFilter = filterStore.CreateLogFilter(filter.FromBlock, filter.ToBlock, filter.Address, filter.Topics, setId: false);
        ArrayPoolList<FilterLog> logs = new(0);
        bool transferred = false;
        OverlayLogsTracer tracer = new(logFilter, logs, specProvider);
        int maxLogs = jsonRpcConfig.MaxLogsPerResponse;
        try
        {
            for (ulong number = from; number <= to; number++)
            {
                token.ThrowIfCancellationRequested();
                Block? block = blockFinder.FindBlock(number, BlockTreeLookupOptions.RequireCanonical);
                if (block is null) return ResultWrapper<IEnumerable<FilterLog>>.Fail($"block {number} not found", ErrorCodes.ResourceNotFound);
                if (block.Transactions.Length == 0) continue;

                if (!TryTrace(new Block(block.Header.Clone(), block.Body), stateOverride, tracer, token, out ResultWrapper<IEnumerable<FilterLog>>? failure)) return failure;
                if (maxLogs > 0 && logs.Count > maxLogs)
                    return ResultWrapper<IEnumerable<FilterLog>>.Fail($"too many logs, max {maxLogs} per response", ErrorCodes.LimitExceeded);
            }

            transferred = true;
            return ResultWrapper<IEnumerable<FilterLog>>.Success(logs);
        }
        catch (InvalidBlockException e)
        {
            return ResultWrapper<IEnumerable<FilterLog>>.Fail(e.Message, ErrorCodes.ExecutionError);
        }
        finally
        {
            if (!transferred) logs.Dispose();
        }
    }

    /// <summary>Re-executes <paramref name="block"/> on its parent state with <paramref name="stateOverride"/> applied.</summary>
    private bool TryTrace<T>(Block block, Dictionary<Address, AccountOverride>? stateOverride, IBlockTracer tracer, CancellationToken token, [NotNullWhen(false)] out ResultWrapper<T>? failure)
    {
        if (!tracerEnv.TryBuildAndOverrideAtTarget(block.Header, stateOverride, null, out Scope<ITracer>? scope))
        {
            failure = ResultWrapper<T>.Fail($"No state available for the parent of block {block.Header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);
            return false;
        }

        using (scope)
        {
            scope.Component.Trace(block, tracer.WithCancellation(token));
        }

        failure = null;
        return true;
    }

    private bool HasCode(BlockHeader header, Address address) =>
        stateReader.TryGetAccount(header, address, out AccountStruct account) && account.HasCode;

    private static ResultWrapper<T> NoState<T>(BlockHeader header) =>
        ResultWrapper<T>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);
}
