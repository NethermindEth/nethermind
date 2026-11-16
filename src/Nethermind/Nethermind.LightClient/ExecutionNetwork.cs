// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Specs;

namespace Nethermind.LightClient;

internal sealed record ExecutionNetwork(string Name, string ChainSpecFile, Hash256 GenesisHash, string? DiscoveryDns,
    NetworkNode[] Bootnodes)
{
    private const string EthereumDnsTree = "enrtree://AKA3AM6LPBYEUDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE@snap.{0}.ethdisco.net";

    internal static ExecutionNetwork Ethereum(string name) => name switch
    {
        "mainnet" => Known(name, "foundation.json", KnownHashes.MainnetGenesis),
        "hoodi" => Known(name, "hoodi.json", KnownHashes.HoodiGenesis),
        "sepolia" => Known(name, "sepolia.json", KnownHashes.SepoliaGenesis),
        _ => throw new ArgumentException("Supported networks: mainnet, hoodi, sepolia.", nameof(name)),
    };

    internal static ExecutionNetwork Custom(string name, string genesisFile, Hash256 genesisHash, string? discoveryDns,
        string? bootnodes)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new ArgumentException("Execution network name must be a plain directory name.", nameof(name));
        NetworkNode[] nodes = bootnodes is null ? [] :
            Array.ConvertAll(bootnodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                static node => new NetworkNode(node));
        if (discoveryDns is null && nodes.Length == 0)
            throw new ArgumentException("A custom execution network needs a discovery DNS tree or bootnodes.");
        return new(name, Path.GetFullPath(genesisFile), genesisHash, discoveryDns, nodes);
    }

    private static ExecutionNetwork Known(string name, string chainSpecFile, Hash256 genesisHash) =>
        new(name, chainSpecFile, genesisHash, string.Format(EthereumDnsTree, name), []);
}
