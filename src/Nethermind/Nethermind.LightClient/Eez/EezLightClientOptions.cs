// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Eez;

internal sealed record EezLightClientOptions(ExecutionNetwork Network, EezSettlement Settlement, int Port)
{
    internal void ValidateSettlementNetwork(string network)
    {
        if (string.Equals(Network.Name, network, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The EEZ execution name must differ from the settlement network name.");
    }

    internal void ValidateRpcPort(int port)
    {
        if (Port == port) throw new ArgumentException("The EEZ execution port must differ from the RPC port.");
    }

    internal static EezLightClientOptions? Read(IConfiguration configuration, params int[] usedPorts)
    {
        string? genesis = configuration["eez-genesis"];
        if (genesis is null) return null;

        Hash256 genesisHash = new(Required("eez-genesis-hash"));
        Address registry = new(Required("eez-registry"));
        ulong rollupId = ulong.Parse(Required("eez-rollup-id"), CultureInfo.InvariantCulture);
        ulong chainId = ulong.Parse(Required("eez-chain-id"), CultureInfo.InvariantCulture);
        if (chainId == 0) throw new ArgumentException("EEZ mode needs a nonzero --eez-chain-id.");
        int port = configuration.GetValue("eez-port", 30308);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException("eez-port", port, "Port must be between 1 and 65535.");
        if (Array.IndexOf(usedPorts, port) >= 0)
            throw new ArgumentException("The EEZ execution port must not overlap the beacon or execution ports.");
        ExecutionNetwork network = ExecutionNetwork.Custom(configuration["eez-name"] ?? "rollupx", genesis, genesisHash,
            configuration["eez-dns"], configuration["eez-bootnodes"]);
        return new(network, new EezSettlement(registry, rollupId, chainId), port);

        string Required(string option) =>
            configuration[option] ?? throw new ArgumentException($"EEZ mode needs --{option}.");
    }
}
