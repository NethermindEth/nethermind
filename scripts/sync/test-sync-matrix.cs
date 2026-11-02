#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include sync.helpers.cs
#:include sync-tests.helpers.cs
#:include ../../.github/actions/gcp-runner/gcp.helpers.cs
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = Cases.Root();
var matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scripts/config/testnet-matrix.json")))!.AsArray();
foreach (var (filter, expected) in new[] { ("hoodi", "hoodi"), ("taiko-hoodi", "taiko-hoodi"), ("taiko", "taiko-alethia,taiko-hoodi"), ("-sepolia", "op-sepolia,world-sepolia"), ("nope", ""), ("", string.Join(',', matrix.Select(entry => entry!["network"]!.ToString()))) })
    Cases.Test($"network selection {filter}", () => Cases.Equal(expected, string.Join(',', Sync.Select(matrix, filter).Select(entry => entry!["network"]!.ToString()))));
Cases.Test("exact selection keeps expanded modes", () => Cases.Equal(2, Sync.Select(JsonNode.Parse("""[{"network":"hoodi","mode":"Flat"},{"network":"hoodi","mode":"HalfPath"},{"network":"taiko-hoodi"}]""")!.AsArray(), "hoodi").Count));
foreach (var network in new[] { "op-sepolia", "world-sepolia", "op-mainnet", "world-mainnet", "hoodi", "sepolia", "mainnet", "gnosis", "chiado" })
    Cases.Test($"L1 mapping {network}", () => Cases.Equal(network.StartsWith("op-", StringComparison.Ordinal) ? network[3..] : network.StartsWith("world-", StringComparison.Ordinal) ? network[6..] : network, Sync.L1(network)));
Cases.Test("matrix required fields and types", () =>
{
    foreach (var entry in matrix)
    {
        foreach (var key in new[] { "network", "timeout", "machine_type", "local_ssd_count", "spot", "cl", "cl_image", "checkpoint-sync-url" }) Cases.True(entry!.AsObject().ContainsKey(key), key);
        Cases.True(entry!["local_ssd_count"]!.GetValue<int>() >= 1, "local SSD required");
        _ = entry["spot"]!.GetValue<bool>();
        Cases.True(MachineFamilyRegex().IsMatch(entry["machine_type"]!.ToString()), "machine family");
    }
    Cases.Equal(0, Sync.ShapeErrors(matrix).Count());
});
foreach (var (type, count, pass) in new[] { ("c2-standard-8", 2, true), ("c3d-standard-8-lssd", 1, true), ("c3d-standard-30-lssd", 2, true), ("c3d-highmem-8-lssd", 1, true), ("c3d-highmem-360-lssd", 32, true), ("c3d-standard-8", 0, true), ("c3-standard-8-lssd", 2, true), ("n2-standard-8", 4, true), ("c3d-standard-8-lssd", 2, false), ("c3d-standard-16-lssd", 2, false), ("c3d-highmem-8-lssd", 2, false), ("c3d-highcpu-8-lssd", 1, false), ("c3d-standard-8", 1, false) })
    Cases.Test($"shape {type} {count}", () =>
    {
        var errors = Sync.ShapeErrors(new JsonArray(new JsonObject { ["network"] = "n", ["machine_type"] = type, ["local_ssd_count"] = count })).ToArray();
        Cases.Equal(pass, errors.Length == 0);
        if (!pass) Cases.True(errors[0].StartsWith($"n: {type}", StringComparison.Ordinal), "error identifies entry");
    });
var expressions = new List<string>();
foreach (var file in new[] { "sync-supported-chains.yml", "sync-master-validation.yml" })
    Cases.Test($"matrix caller {file}", () =>
    {
        var text = File.ReadAllText(Path.Combine(root, ".github/workflows", file));
        Cases.True(text.IndexOf("$machine_type", StringComparison.Ordinal) < text.IndexOf("check-runner-shapes.cs", StringComparison.Ordinal), "shape validation follows override");
        Cases.True(text.Contains("select-networks.cs", StringComparison.Ordinal), "network selector invoked");
        var expression = ProvisioningModelRegex().Match(text);
        Cases.True(expression.Success, "provisioning expression exists");
        expressions.Add(WhitespaceRegex().Replace(expression.Groups[1].Value, " ").Trim());
    });
Cases.Test("matrix provisioning expressions agree", () => Cases.Equal(expressions[0], expressions[1]));
foreach (var (model, config, expected) in new[] { ("Default", "{}", "SPOT"), ("Default", "{\"spot\":null}", "SPOT"), ("Default", "{\"spot\":true}", "SPOT"), ("Default", "{\"spot\":false}", "STANDARD"), ("", "{}", "SPOT"), ("Spot", "{\"spot\":false}", "SPOT"), ("Standard", "{\"spot\":true}", "STANDARD") })
    await Cases.Test($"provisioning {model} {config}", async () =>
    {
        var result = await Gcp.Run("jq", ["-nr", "--arg", "model", model, "--argjson", "config", config, expressions[0]], null, CancellationToken.None);
        Cases.Equal(0, result.Code);
        Cases.Equal(expected, result.Output.Trim());
    });
Cases.Finish();

partial class Program
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"provisioning_model: \((.*?)\n\s*\),", RegexOptions.Singleline)]
    private static partial Regex ProvisioningModelRegex();
    [GeneratedRegex("^(c2|c3d)-")]
    private static partial Regex MachineFamilyRegex();
}
