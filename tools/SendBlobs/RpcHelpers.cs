// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;

namespace SendBlobs;

internal static class RpcHelpers
{
    private const string DefaultHexQuantity = "0x1";

    public static async Task<ulong> GetChainIdAsync(IJsonRpcClient rpcClient)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);

        string chainIdString = await rpcClient.Post<string>("eth_chainId") ?? "1";
        return HexConvert.ToUInt64(chainIdString);
    }

    public static async Task<ulong?> GetNonceAsync(IJsonRpcClient rpcClient, Address address)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);

        string? nonceString = await rpcClient.Post<string>("eth_getTransactionCount", address, "latest");
        return nonceString is null ? null : HexConvert.ToUInt64(nonceString);
    }

    public static async Task<UInt256> GetGasPriceAsync(IJsonRpcClient rpcClient)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);

        string gasPriceString = await rpcClient.Post<string>("eth_gasPrice") ?? DefaultHexQuantity;
        return UInt256.Parse(gasPriceString);
    }

    public static async Task<UInt256> GetMaxPriorityFeePerGasAsync(IJsonRpcClient rpcClient)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);

        string maxPriorityFeeString = await rpcClient.Post<string>("eth_maxPriorityFeePerGas") ?? DefaultHexQuantity;
        return UInt256.Parse(maxPriorityFeeString);
    }
}

