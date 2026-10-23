#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include BenchmarkState.Helpers.cs
#:include RpcScriptTests.Helpers.cs

using var tests = new RpcScriptTests();
foreach (var value in new[] { "", "plain", "/data/path with space", "x'y\"z", "--arg=value;other", "dollar$(touch x)", "`command`", "nonascii-λ" })
    tests.Case($"state roundtrip {value}", () => RpcScriptTests.Assert(BenchmarkState.Decode(BenchmarkState.Encode(value)) == value, "state value changed"));
foreach (var (encoded, expected) in new[] { ("''", ""), ("one\\ two", "one two"), ("'single quoted'", "single quoted"), ("\"double quoted\"", "double quoted"), ("$'tab\\tline\\n'", "tab\tline\n"), ("$'\\x41\\101\\u0042'", "AAB") })
    tests.Case($"historical shell quoting {encoded}", () => RpcScriptTests.Assert(BenchmarkState.Decode(encoded) == expected, "historical state changed"));
foreach (var encoded in new[] { "$(touch /tmp/x)", "`id`", "\"$HOME\"", "value;command", "value|command", "unterminated\\", "'unterminated" })
    tests.Case($"reject executable state {encoded}", () =>
    {
        try { BenchmarkState.Decode(encoded); }
        catch (IOException) { return; }
        throw new InvalidOperationException("executable state accepted");
    });
tests.Case("last state assignment wins without evaluation", () =>
{
    var path = tests.Write("node.env", "# state\nNAME=first\nEMPTY=''\nNAME=second\n");
    BenchmarkState.Append(path, "PATH", "/data/a b");
    var values = BenchmarkState.Read(path);
    RpcScriptTests.Assert(values["NAME"] == "second" && values["EMPTY"] == "" && values["PATH"] == "/data/a b", "state merge changed");
});
tests.Complete();
