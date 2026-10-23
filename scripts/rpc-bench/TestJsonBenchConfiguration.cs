#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include JsonBenchConfiguration.Helpers.cs
#:include RpcScriptTests.Helpers.cs

using YamlDotNet.RepresentationModel;

using var tests = new RpcScriptTests();
tests.Case("generated read mix preserves weights and thresholds", () =>
{
    var config = JsonBenchConfiguration.Render("", "primary", "reference", "", "eth_call", _ => "");
    var calls = (YamlSequenceNode)config.Children[new YamlScalarNode("calls")];
    RpcScriptTests.Assert(calls.Children.Count == 5 && calls.Children.Cast<YamlMappingNode>().All(call => call.Children.ContainsKey(new YamlScalarNode("thresholds"))), "default calls changed");
});
tests.Case("custom config preserves extra fields and explicit thresholds", () =>
{
    var path = tests.Write("custom.yaml", "duration: 1s\nextra: keep\ncalls:\n  - method: eth_call\n    thresholds: [custom]\n");
    var config = JsonBenchConfiguration.Render(path, "primary", "", "", "eth_call", name => name == "JB_RPS" ? "42" : "");
    RpcScriptTests.Assert(config.Children[new YamlScalarNode("extra")].ToString() == "keep" && config.Children[new YamlScalarNode("rps")].ToString() == "42", "custom configuration lost");
});
tests.Case("corpus calls reference private fixture files with aggregate weights", () =>
{
    var path = tests.Write("classes.json", "{\"class_0\":3,\"class_1\":2}");
    var config = JsonBenchConfiguration.Render("", "primary", "", path, "debug_traceCall", _ => "");
    var output = Path.Combine(tests.DirectoryPath, "benchmark.yaml"); JsonBenchConfiguration.Save(output, config);
    var text = File.ReadAllText(output);
    RpcScriptTests.Assert(text.Contains("debug_traceCall class_0") && text.Contains("./rpc-calls/corpus/class_0.json") && text.Contains("weight: 3"), text);
});
tests.Complete();
