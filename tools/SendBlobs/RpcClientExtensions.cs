// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules.Eth;

namespace SendBlobs;

internal static class RpcClientExtensions
{
    private const string Latest = "latest";
    private const int MaxRetryCount = 3;

    extension(IJsonRpcClient rpcClient)
    {
        public async Task<ulong> GetChainIdAsync()
        {
            string chainIdString = await PostWithRetry<string>(rpcClient, nameof(IEthRpcModule.eth_chainId));

            return Bytes.FromHexString(chainIdString).AsSpan().ReadEthUInt64();
        }

        public async Task<ulong> GetTransactionCountAsync(Address address)
        {
            string nonceString = await PostWithRetry<string>(rpcClient, nameof(IEthRpcModule.eth_getTransactionCount), address, Latest);

            return Bytes.FromHexString(nonceString).AsSpan().ReadEthUInt64();
        }

        public async Task<UInt256> GetGasPriceAsync()
        {
            string gasPriceRes = await PostWithRetry<string>(rpcClient, nameof(IEthRpcModule.eth_gasPrice));

            return UInt256.Parse(gasPriceRes);
        }

        public async Task<UInt256> GetMaxPriorityFeePerGasAsync()
        {
            string maxPriorityFeeRes = await PostWithRetry<string>(rpcClient, nameof(IEthRpcModule.eth_maxPriorityFeePerGas));

            return UInt256.Parse(maxPriorityFeeRes);
        }

        public async Task<bool> IsNodeSyncedAsync()
        {
            object syncingResult = await PostWithRetry<object>(rpcClient, nameof(IEthRpcModule.eth_syncing));

            return syncingResult is bool isSyncing && isSyncing is false;
        }

        public async Task<string> GetBalanceAsync(Address address, string blockTag = Latest) => await PostWithRetry<string>(rpcClient, nameof(IEthRpcModule.eth_getBalance), address, blockTag);

        public async Task<string> SendRawTransactionAsync(string txRlpHex) => await PostWithRetry<string>(rpcClient, nameof(IEthRpcModule.eth_sendRawTransaction), txRlpHex);

        public async Task<BlockModel<Hash256>?> GetBlockByNumberAsync(object blockNumberOrTag, bool fullTxObjects) => await PostWithRetryNullable<BlockModel<Hash256>>(rpcClient, nameof(IEthRpcModule.eth_getBlockByNumber), blockNumberOrTag, fullTxObjects);

        private async Task<T> PostWithRetry<T>(string method, params object?[]? parameters) =>
            (await PostWithRetryNullable<T>(rpcClient, method, parameters: parameters)) ?? throw new RpcException($"RPC call '{nameof(IEthRpcModule.eth_chainId)}' returned null or empty response.");

        private async Task<T?> PostWithRetryNullable<T>(string method, params object?[]? parameters)
        {
            Exception? lastException = null;

            for (int attempt = 1; attempt <= MaxRetryCount; attempt++)
            {
                try
                {
                    object?[] safeParameters = parameters ?? [];
                    return await rpcClient.Post<T>(method, safeParameters);
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    if (attempt >= MaxRetryCount)
                    {
                        break;
                    }

                    int delayMs = 300 * attempt;
                    await Task.Delay(delayMs);
                }
            }

            string baseMessage = $"RPC call '{method}' failed after {MaxRetryCount} attempts.";

            if (lastException is null)
            {
                throw new RpcException(baseMessage);
            }

            throw new RpcException($"{baseMessage} {lastException.Message}", lastException);
        }
    }
}

public class RpcException(string message, Exception? innerException = null) : Exception(message, innerException);
