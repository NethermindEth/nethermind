#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include NodeConfiguration.Helpers.cs
#:include RpcScriptTests.Helpers.cs

using var tests = new RpcScriptTests();
foreach (var client in new[] { "nethermind", "geth", "reth" })
    tests.Case($"{client} isolated benchmark defaults", () =>
    {
        var arguments = NodeConfiguration.Arguments((name, fallback) => name == "CLIENT" ? client : fallback);
        RpcScriptTests.Assert(arguments.Contains("--datadir=/execution-data"), "datadir lost");
        RpcScriptTests.Assert(arguments.Contains(client == "nethermind" ? "--JsonRpc.GasCap=1000000000" : "--rpc.gascap=1000000000"), "gas cap changed");
        RpcScriptTests.Assert(arguments.Contains(client switch { "nethermind" => "--Network.MaxActivePeers=0", "geth" => "--maxpeers=0", _ => "--max-outbound-peers=0" }), "peers not disabled");
        RpcScriptTests.Assert(arguments.Contains(client == "nethermind" ? "--JsonRpc.Port=8545" : "--http.port=8545"), "RPC port changed");
    });
foreach (var client in new[] { "geth", "reth" })
    tests.Case($"{client} rejects unsupported networks", () =>
    {
        try { NodeConfiguration.Arguments((name, fallback) => name switch { "CLIENT" => client, "NETWORK" => "other", _ => fallback }); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("unsupported network accepted");
    });
tests.Case("flags split only on whitespace without glob or shell evaluation", () =>
{
    var arguments = NodeConfiguration.Arguments((name, fallback) => name switch { "LAYOUT_FLAGS" => "--layout=flat\n--wildcard=*", "ADDITIONAL_FLAGS" => "--x=$HOME --semicolon=a;b\t--last=value", _ => fallback });
    RpcScriptTests.Assert(arguments.TakeLast(5).SequenceEqual(["--layout=flat", "--wildcard=*", "--x=$HOME", "--semicolon=a;b", "--last=value"]), string.Join(' ', arguments));
});
tests.Case("module, datadir and gas options propagate as single arguments", () =>
{
    var arguments = NodeConfiguration.Arguments((name, fallback) => name switch { "DATA_DIR_TARGET" => "/path with spaces", "JSONRPC_MODULES" => "Eth,Debug", "RPC_GAS_CAP" => "42", _ => fallback });
    RpcScriptTests.Assert(arguments.Contains("--datadir=/path with spaces") && arguments.Contains("--JsonRpc.EnabledModules=Eth,Debug") && arguments.Contains("--JsonRpc.GasCap=42"), string.Join(' ', arguments));
});
tests.Case("unsupported client fails", () =>
{
    try { NodeConfiguration.Arguments((name, fallback) => name == "CLIENT" ? "unsupported" : fallback); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException("unsupported client accepted");
});
tests.Complete();
