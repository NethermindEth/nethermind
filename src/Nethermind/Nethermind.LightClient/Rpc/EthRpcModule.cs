// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class EthRpcModule(RpcService service, ILogManager logs) : IEthRpcModule
{
    // Preserve existing log filters when the implementation class is renamed.
    private readonly Nethermind.Logging.ILogger _logger = logs.GetLogger("LightClient.Rpc.VerifiedEthRpcModule");

    public ResultWrapper<ulong> eth_chainId() => ResultWrapper<ulong>.Success(service.ChainId);
    public ResultWrapper<Address[]> eth_accounts() => ResultWrapper<Address[]>.Success([]);
    public ResultWrapper<SyncingResult> eth_syncing() => ResultWrapper<SyncingResult>.Success(service.GetSyncing());
    public Task<ResultWrapper<ulong>> eth_blockNumber() => Execute(static (rpc, token) => rpc.GetBlockNumberAsync(token));
    public Task<ResultWrapper<UInt256>> eth_blobBaseFee() => Execute(static (rpc, token) => rpc.GetBlobBaseFeeAsync(token));
    public Task<ResultWrapper<UInt256>> eth_gasPrice() => Execute(static (rpc, token) => rpc.GetGasPriceAsync(false, token));
    public Task<ResultWrapper<UInt256>> eth_maxPriorityFeePerGas() => Execute(static (rpc, token) => rpc.GetGasPriceAsync(true, token));
    public Task<ResultWrapper<UInt256>> eth_getBalance(Address address, JsonElement? blockParameter = null) =>
        Execute((rpc, token) => rpc.GetBalanceAsync(address, blockParameter ?? default, token));
    public Task<ResultWrapper<ulong>> eth_getTransactionCount(Address address, JsonElement? blockParameter = null) =>
        Execute((rpc, token) => rpc.GetNonceAsync(address, blockParameter ?? default, token));
    public Task<ResultWrapper<byte[]>> eth_getCode(Address address, JsonElement? blockParameter = null) =>
        Execute((rpc, token) => rpc.GetCodeAsync(address, blockParameter ?? default, token));
    public Task<ResultWrapper<byte[]>> eth_getStorageAt(Address address, StorageIndex position, JsonElement? blockParameter = null) =>
        Execute((rpc, token) => rpc.GetStorageAsync(address, position.Value, blockParameter ?? default, token));
    public Task<ResultWrapper<AccountProof>> eth_getProof(Address address, StorageIndex[] storageKeys, JsonElement? blockParameter = null) =>
        Execute((rpc, token) => rpc.GetProofAsync(address, storageKeys, blockParameter ?? default, token));
    public Task<ResultWrapper<Hash256>> eth_sendRawTransaction(byte[] transaction) =>
        Execute((rpc, token) => rpc.SendRawTransactionAsync(transaction, token), waitForStartup: false);
    public Task<ResultWrapper<JsonElement>> eth_sendRawTransactionSync(byte[] transaction, ulong? timeoutMs = null) =>
        Execute((rpc, token) => rpc.SendRawTransactionSyncAsync(transaction, timeoutMs, token), waitForStartup: false);
    public Task<ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>>> eth_simulateV1(JsonElement payload, JsonElement? blockParameter = null) =>
        Execute((rpc, token) => rpc.SimulateAsync(payload, blockParameter ?? default, token));
    public Task<ResultWrapper<JsonElement>> eth_feeHistory(UInt256 blockCount, JsonElement newestBlock, double[] rewardPercentiles) =>
        Execute((rpc, token) => rpc.GetFeeHistoryAsync(blockCount, newestBlock, rewardPercentiles, token));
    public Task<ResultWrapper<JsonElement>> eth_getBlockByNumber(JsonElement blockParameter, bool returnFullTransactionObjects) =>
        Execute((rpc, token) => rpc.GetBlockAsync(blockParameter, false, returnFullTransactionObjects, token));
    public Task<ResultWrapper<JsonElement>> eth_getBlockByHash(Hash256 blockHash, bool returnFullTransactionObjects) =>
        Execute((rpc, token) => rpc.GetBlockAsync(HashParameter(blockHash), true, returnFullTransactionObjects, token));
    public Task<ResultWrapper<JsonElement>> eth_getBlockTransactionCountByNumber(JsonElement blockParameter) =>
        Execute((rpc, token) => rpc.GetBlockTransactionCountAsync(blockParameter, false, token));
    public Task<ResultWrapper<JsonElement>> eth_getBlockTransactionCountByHash(Hash256 blockHash) =>
        Execute((rpc, token) => rpc.GetBlockTransactionCountAsync(HashParameter(blockHash), true, token));
    public Task<ResultWrapper<JsonElement>> eth_getTransactionByBlockNumberAndIndex(JsonElement blockParameter, UInt256 position) =>
        Execute((rpc, token) => rpc.GetTransactionAsync(blockParameter, false, position, token));
    public Task<ResultWrapper<JsonElement>> eth_getTransactionByBlockHashAndIndex(Hash256 blockHash, UInt256 position) =>
        Execute((rpc, token) => rpc.GetTransactionAsync(HashParameter(blockHash), true, position, token));
    public Task<ResultWrapper<JsonElement>> eth_getTransactionByHash(Hash256 transactionHash) =>
        Execute((rpc, token) => rpc.GetTransactionByHashAsync(transactionHash, ExecutionData.TransactionResult.Transaction, token));
    public Task<ResultWrapper<JsonElement>> eth_getTransactionReceipt(Hash256 transactionHash) =>
        Execute((rpc, token) => rpc.GetTransactionByHashAsync(transactionHash, ExecutionData.TransactionResult.Receipt, token));
    public Task<ResultWrapper<JsonElement>> eth_getBlockReceipts(JsonElement blockParameter) =>
        Execute((rpc, token) => rpc.GetBlockReceiptsAsync(blockParameter, token));
    public Task<ResultWrapper<JsonElement>> eth_getLogs(JsonElement filter) =>
        Execute((rpc, token) => rpc.GetLogsDataAsync(filter, token));

    private Task<ResultWrapper<T>> Execute<T>(Func<RpcBackend, CancellationToken, Task<T>> invoke, bool waitForStartup = true) =>
        Execute(invoke, static result => ResultWrapper<T>.Success(result), waitForStartup);

    private Task<ResultWrapper<T>> Execute<T, TResult>(Func<RpcBackend, CancellationToken, Task<TResult>> invoke,
        Func<TResult, ResultWrapper<T>> wrap, bool waitForStartup = true)
    {
        // Current is thread-static and only belongs to this request before the first await.
        CancellationToken token = JsonRpcContext.Current?.RequestAborted ?? default;
        return ExecuteAsync(invoke, wrap, token, waitForStartup);
    }

    private async Task<ResultWrapper<T>> ExecuteAsync<T, TResult>(Func<RpcBackend, CancellationToken, Task<TResult>> invoke,
        Func<TResult, ResultWrapper<T>> wrap, CancellationToken token, bool waitForStartup)
    {
        try
        {
            RpcBackend backend = await service.GetBackendAsync(token, waitForStartup);
            return wrap(await invoke(backend, token));
        }
        catch (RpcException exception)
        {
            return exception.RevertData is null
                ? ResultWrapper<T>.Fail(exception.Message, exception.Code)
                : ResultWrapper<T, string>.Fail(exception.Message, exception.Code, exception.RevertData);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ExecutionDataUnavailableException exception)
        {
            if (_logger.IsDebug) _logger.Debug($"Authenticated RPC data is unavailable: {exception.Message}");
            return ResultWrapper<T>.Fail("Requested authenticated data is unavailable.", -32001);
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        { return ResultWrapper<T>.Fail("Invalid parameters.", -32602); }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            if (_logger.IsWarn) _logger.Warn($"Unable to produce a verified RPC response: {exception.Message}");
            return ResultWrapper<T>.Fail("Unable to verify peer data", -32000);
        }
    }

    private static JsonElement HashParameter(Hash256 hash) => JsonSerializer.SerializeToElement(hash.ToString());
}
