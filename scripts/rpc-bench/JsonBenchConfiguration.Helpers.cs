// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

internal static class JsonBenchConfiguration
{
    internal static YamlMappingNode Render(string source, string primary, string reference, string classesPath, string method, Func<string, string> environment)
    {
        YamlMappingNode config;
        if (source.Length > 0)
        {
            using var input = File.OpenText(source); var yaml = new YamlStream(); yaml.Load(input);
            config = yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is YamlScalarNode { Value: "" or null or "null" } ? [] : yaml.Documents[0].RootNode as YamlMappingNode ?? throw new IOException("benchmark config must be a mapping");
        }
        else config = (YamlMappingNode)ToYaml(JsonNode.Parse("""
            {"test_name":"RPC read benchmark","description":"Snapshot-backed read-path benchmark","duration":"60s","rps":100,"vus":10,"calls":[
              {"name":"WETH balance eth_call","method":"eth_call","weight":40,"params":[{"to":"0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2","data":"0x70a08231000000000000000000000000000000000000000000000000000000000000000a"}]},
              {"name":"eth_getBalance","method":"eth_getBalance","weight":20,"params":["0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045","latest"]},
              {"name":"eth_blockNumber","method":"eth_blockNumber","weight":20,"params":[]},
              {"name":"eth_getTransactionCount","method":"eth_getTransactionCount","weight":10,"params":["0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045","latest"]},
              {"name":"eth_getBlockByNumber","method":"eth_getBlockByNumber","weight":10,"params":["latest",false]}
            ]}
            """));
        var clients = new YamlSequenceNode { primary }; if (reference.Length > 0) clients.Add(reference); config.Children[new YamlScalarNode("clients")] = clients;
        foreach (var key in new[] { "rps", "vus", "seed" })
        {
            var value = environment("JB_" + key.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(value)) config.Children[new YamlScalarNode(key)] = new YamlScalarNode(long.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        }
        var duration = environment("JB_DURATION"); if (!string.IsNullOrWhiteSpace(duration)) config.Children[new YamlScalarNode("duration")] = new YamlScalarNode(duration);
        if (classesPath.Length > 0)
        {
            var classes = JsonNode.Parse(File.ReadAllText(classesPath))!.AsObject();
            var calls = new YamlSequenceNode();
            foreach (var (name, count) in classes)
                calls.Add(new YamlMappingNode { { "name", (method == "eth_call" ? "" : method + " ") + name }, { "file", "./rpc-calls/corpus/" + name + ".json" }, { "file_type", "json" }, { "weight", count!.ToJsonString() } });
            config.Children[new YamlScalarNode("calls")] = calls;
        }
        if (config.Children.TryGetValue(new YamlScalarNode("calls"), out var entries) && entries is YamlSequenceNode sequence)
            foreach (var call in sequence.Children.OfType<YamlMappingNode>())
                if (!call.Children.ContainsKey(new YamlScalarNode("thresholds"))) call.Add("thresholds", new YamlSequenceNode { "p(99)<600000" });
        return config;
    }

    internal static void Save(string path, YamlMappingNode config)
    {
        using var output = File.CreateText(path); new YamlStream(new YamlDocument(config)).Save(output, assignAnchors: false);
    }

    private static YamlNode ToYaml(JsonNode? node) => node switch
    {
        JsonObject obj => new YamlMappingNode(obj.Select(pair => KeyValuePair.Create<YamlNode, YamlNode>(new YamlScalarNode(pair.Key), ToYaml(pair.Value)))),
        JsonArray array => new YamlSequenceNode(array.Select(ToYaml)),
        JsonValue value when value.TryGetValue<string>(out var text) => new YamlScalarNode(text) { Style = YamlDotNet.Core.ScalarStyle.DoubleQuoted },
        null => new YamlScalarNode("null"),
        _ => new YamlScalarNode(node.ToJsonString())
    };
}
