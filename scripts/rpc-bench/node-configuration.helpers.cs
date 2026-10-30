// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

internal static class NodeConfiguration
{
    internal static List<string> Arguments(Func<string, string, string> get)
    {
        var client = get("CLIENT", "nethermind");
        var network = get("NETWORK", "mainnet");
        var data = get("DATA_DIR_TARGET", "/execution-data");
        var gasCap = get("RPC_GAS_CAP", "1000000000");
        if (client is "geth" or "reth" && network != "mainnet") throw new ArgumentException($"CLIENT={client} supports only network=mainnet (got '{network}')");
        var result = client switch
        {
            "nethermind" => new List<string>
            {
                "--datadir=" + data, "--config=" + network, "--Init.BaseDbPath=" + network,
                "--JsonRpc.Enabled=true", "--JsonRpc.Host=0.0.0.0", "--JsonRpc.Port=8545",
                "--JsonRpc.EnabledModules=" + get("JSONRPC_MODULES", "Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug"),
                "--JsonRpc.Timeout=600000", "--JsonRpc.GasCap=" + gasCap, "--Init.DiscoveryEnabled=false", "--Network.MaxActivePeers=0",
                "--Pruning.Mode=None", "--HealthChecks.Enabled=false", "--Metrics.Enabled=false"
            },
            "geth" => ["--datadir=" + data, "--http", "--http.addr=0.0.0.0", "--http.port=8545", "--http.api=" + get("GETH_HTTP_API", "eth,net,web3,debug,txpool"), "--http.vhosts=*", "--rpc.gascap=" + gasCap, "--nodiscover", "--maxpeers=0", "--ipcdisable"],
            "reth" => ["node", "--datadir=" + data, "--http", "--http.addr=0.0.0.0", "--http.port=8545", "--http.api=" + get("RETH_HTTP_API", "eth,net,web3,debug,trace,txpool"), "--rpc.gascap=" + gasCap, "--disable-discovery", "--max-outbound-peers=0", "--max-inbound-peers=0"],
            _ => throw new ArgumentException($"unknown CLIENT '{client}' (expected nethermind | geth | reth)")
        };
        if (client == "nethermind") result.AddRange(SplitFlags(get("LAYOUT_FLAGS", "")));
        result.AddRange(SplitFlags(get("ADDITIONAL_FLAGS", "")));
        return result;
    }

    internal static string[] SplitFlags(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
