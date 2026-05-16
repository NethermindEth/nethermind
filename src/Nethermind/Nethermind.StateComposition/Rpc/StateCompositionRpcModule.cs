// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.JsonRpc;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Service;

namespace Nethermind.StateComposition.Rpc;

internal sealed class StateCompositionRpcModule(
    StateCompositionService service,
    StateCompositionStateHolder stateHolder,
    IBlockTree blockTree,
    IStateCompositionConfig config)
    : IStateCompositionRpcModule
{
    public Task<ResultWrapper<StateCompositionReport>> statecomp_get() => Task.FromResult(
            ResultWrapper<StateCompositionReport>.Success(stateHolder.BuildReport()));

    public Task<ResultWrapper<bool>> statecomp_cancelScan() =>
        Task.FromResult(ResultWrapper<bool>.Success(service.CancelScan()));

    public async Task<ResultWrapper<TopContractEntry?>> statecomp_inspectContract(Address? address)
    {
        if (address is null)
            return ResultWrapper<TopContractEntry?>.Fail("Address parameter is required", ErrorCodes.InvalidInput);

        Block? head = blockTree.Head;
        if (head is null)
            return ResultWrapper<TopContractEntry?>.Fail("No head block available", ErrorCodes.ResourceUnavailable);

        // JsonRpcContext does not flow a request-scoped CancellationToken, so bound
        // the walk here with a config-driven deadline. Without this, a slow storage
        // trie can pin an RPC worker indefinitely even after the caller has hung up.
        int timeoutSeconds = config.InspectContractTimeoutSeconds;
        using CancellationTokenSource cts = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();

        try
        {
            Result<TopContractEntry?> result = await service.InspectContractAsync(
                address, head.Header, cts.Token).ConfigureAwait(false);
            return !result.Success(out TopContractEntry? entry, out string? error)
                ? ResultWrapper<TopContractEntry?>.Fail(error, ErrorCodes.ResourceUnavailable)
                : ResultWrapper<TopContractEntry?>.Success(entry);
        }
        catch (OperationCanceledException)
        {
            return cts.IsCancellationRequested && timeoutSeconds > 0
                ? ResultWrapper<TopContractEntry?>.Fail(
                    $"Inspection timed out after {timeoutSeconds}s", ErrorCodes.Timeout)
                : ResultWrapper<TopContractEntry?>.Fail("Inspection was cancelled");
        }
    }

}
