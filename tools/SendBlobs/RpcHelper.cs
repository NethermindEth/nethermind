// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;

namespace SendBlobs;

internal static class RpcHelper
{
    public static async Task<ulong> GetChainIdAsync(IJsonRpcClient rpcClient)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);

        string chainIdString = await rpcClient.Post<string>("eth_chainId") ?? "0x1";
        return Bytes.FromHexString(chainIdString).AsSpan().ReadEthUInt64();
    }

    public static async Task<ulong?> GetTransactionCountAsync(IJsonRpcClient rpcClient, Address address)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);

        string? nonceString = await rpcClient.Post<string>("eth_getTransactionCount", address, "latest");
        return nonceString is null ? null : Bytes.FromHexString(nonceString).AsSpan().ReadEthUInt64();
    }

    public static async Task<UInt256> GetGasPriceAsync(IJsonRpcClient rpcClient)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);

        string gasPriceRes = await rpcClient.Post<string>("eth_gasPrice") ?? "0x1";
        return UInt256.Parse(gasPriceRes);
    }

    public static async Task<UInt256> GetMaxPriorityFeePerGasAsync(IJsonRpcClient rpcClient)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);

        string maxPriorityFeeRes = await rpcClient.Post<string>("eth_maxPriorityFeePerGas") ?? "0x1";
        return UInt256.Parse(maxPriorityFeeRes);
    }

    public static async Task<bool> IsNodeSyncedAsync(IJsonRpcClient rpcClient)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);

        object? syncingResult = await rpcClient.Post<object>("eth_syncing");
        return syncingResult is bool isSyncing && isSyncing is false;
    }
}
