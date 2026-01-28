// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;

namespace SendBlobs;

internal static class RpcClientExtensions
{
    extension(IJsonRpcClient rpcClient)
    {
        public async Task<ulong> GetChainIdAsync()
        {
            ArgumentNullException.ThrowIfNull(rpcClient);

            string chainIdString = await rpcClient.Post<string>("eth_chainId") ?? "0x1";
            return Bytes.FromHexString(chainIdString).AsSpan().ReadEthUInt64();
        }

        public async Task<ulong?> GetTransactionCountAsync(Address address)
        {
            ArgumentNullException.ThrowIfNull(rpcClient);

            string? nonceString = await rpcClient.Post<string>("eth_getTransactionCount", address, "latest");
            return nonceString is null ? null : Bytes.FromHexString(nonceString).AsSpan().ReadEthUInt64();
        }

        public async Task<UInt256> GetGasPriceAsync()
        {
            ArgumentNullException.ThrowIfNull(rpcClient);

            string gasPriceRes = await rpcClient.Post<string>("eth_gasPrice") ?? "0x1";
            return UInt256.Parse(gasPriceRes);
        }

        public async Task<UInt256> GetMaxPriorityFeePerGasAsync()
        {
            ArgumentNullException.ThrowIfNull(rpcClient);

            string maxPriorityFeeRes = await rpcClient.Post<string>("eth_maxPriorityFeePerGas") ?? "0x1";
            return UInt256.Parse(maxPriorityFeeRes);
        }

        public async Task<bool> IsNodeSyncedAsync()
        {
            ArgumentNullException.ThrowIfNull(rpcClient);

            object? syncingResult = await rpcClient.Post<object>("eth_syncing");
            return syncingResult is bool isSyncing && isSyncing is false;
        }

        public async Task<string?> GetBalanceAsync(Address address, string blockTag = "latest")
        {
            ArgumentNullException.ThrowIfNull(rpcClient);

            return await rpcClient.Post<string>("eth_getBalance", address, blockTag);
        }

        public async Task<string?> SendRawTransactionAsync(string txRlpHex)
        {
            ArgumentNullException.ThrowIfNull(rpcClient);

            return await rpcClient.Post<string>("eth_sendRawTransaction", txRlpHex);
        }

        public async Task<BlockModel<Hash256>?> GetBlockByNumberAsync(object blockNumberOrTag, bool fullTxObjects)
        {
            ArgumentNullException.ThrowIfNull(rpcClient);

            return await rpcClient.Post<BlockModel<Hash256>>("eth_getBlockByNumber", blockNumberOrTag, fullTxObjects);
        }
    }
}
