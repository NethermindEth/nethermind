#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include ../Profiles.Helpers.cs
#:include RpcScriptTests.Helpers.cs

using System.Text.RegularExpressions;

using var tests = new RpcScriptTests();
var managed = "instance void [Nethermind.Trie] Nethermind.Trie.TrieStore`1<class System.Object>::Commit(int64)";
tests.Case("folding preserves unknown DSOs, managed generic names, trailing whitespace and sample order", () =>
{
    using var input = new StringReader($".NET 100 cycles:\n\t0000 [unknown] (/usr/lib/libmystery.so)\n\n.NET 101 cycles:\n\t0000 {managed}+0x1 (/tmp/perf-101.map)\n\t0001 root+0x2 (/tmp/perf-101.map)\n\n.NET 100 cycles:\n\t0000 [unknown] (/usr/lib/libmystery.so)  \n");
    var counts = Profiles.Fold(input);
    RpcScriptTests.Assert(counts.Count == 2 && counts[".NET;[unknown] (libmystery.so)"] == 2 && counts[$".NET;root;{managed}"] == 1, "folded stacks differ");
});
var valid = tests.Write("valid.folded", $".NET;{managed} 7\n.NET;rocksdb::DBImpl::BackgroundCall 5\n.NET;[unknown] (librocksdb.so) 3\n");
tests.Case("native view excludes generic managed frames", () =>
{
    var result = RpcScriptTests.Run("scripts/PerfReport.cs", "native", valid, "10");
    RpcScriptTests.Assert(result.Code == 0 && !result.Output.Contains(managed) && result.Output.Contains("rocksdb::DBImpl::BackgroundCall") && result.Output.Contains("[unknown] (librocksdb.so)"), result.ToString());
});
tests.Case("inclusive totals deduplicate recursion", () =>
{
    var path = tests.Write("recursion.folded", "root;recursive;recursive;leaf 5\nroot;leaf 2\n");
    var (counts, total) = Profiles.Aggregate(path, true);
    RpcScriptTests.Assert(total == 7 && counts["recursive"] == 5 && counts["leaf"] == 7, "recursion double counted");
});
tests.Case("total label and truncation preserve method tail", () =>
{
    var path = tests.Write("long.folded", $"root;{new string('x', 150)}::MethodNameTailForDiscrimination(int32) 5\n");
    foreach (var view in new[] { "top", "total" })
    {
        var result = RpcScriptTests.Run("scripts/PerfReport.cs", view, path, "1");
        RpcScriptTests.Assert(result.Code == 0 && result.Output.Contains(view == "top" ? "Self %" : "Total %") && result.Output.Contains("MethodNameTailForDiscrimination"), result.ToString());
    }
});
var before = tests.Write("before.folded", "root;Negative 50\nroot;Middle 10\nroot;Positive 10\nroot;Other 20\nroot;Small 10\n");
var after = tests.Write("after.folded", "root;Negative 5\nroot;Middle 10\nroot;Positive 55\nroot;Other 20\nroot;Small 10\n");
foreach (var (limit, expected) in new[] { (1, 2), (3, 4) })
    tests.Case($"comparison limit {limit} preserves both tails", () =>
    {
        var result = RpcScriptTests.Run("scripts/PerfReport.cs", "compare", before, after, limit.ToString());
        RpcScriptTests.Assert(result.Code == 0 && Regex.Matches(result.Output, @"\s\d+\.\d+%\s+\d+\.\d+%\s+[+-]\d+\.\d+").Count == expected && result.Output.Contains("Negative") && result.Output.Contains("Positive") && result.Output.Contains("..."), result.ToString());
    });
foreach (var (name, contents) in new[] { ("empty", ""), ("whitespace", " \t\n\n"), ("zero", ".NET;Frame 0\n") })
    tests.Case($"validator rejects {name}", () =>
    {
        var result = RpcScriptTests.Run("scripts/ValidateFoldedProfile.cs", tests.Write(name + ".folded", contents));
        RpcScriptTests.Assert(result.Code != 0 && result.Error.Contains("no positive-sample stack"), result.ToString());
    });
foreach (var (name, contents, code, split) in new[]
{
    ("unknown", ".NET;[unknown] (libcoreclr.so) 7\n", 1, "unknown=7 (100.00%)"),
    ("native", ".NET;rocksdb::DBImpl::BackgroundCall 5\n", 1, "native=5 (100.00%)"),
    ("mixed", $".NET;{managed} 5\n.NET;rocksdb::DBImpl::BackgroundCall 3\n.NET;[unknown] (libcoreclr.so) 2\n", 0, "managed=5 (50.00%), native=3 (30.00%), unknown=2 (20.00%)")
})
    tests.Case($"validator reports {name} leaf split", () =>
    {
        var result = RpcScriptTests.Run("scripts/ValidateFoldedProfile.cs", tests.Write(name + ".folded", contents));
        RpcScriptTests.Assert(result.Code == code && result.Output.Contains(split), result.ToString());
    });
foreach (var view in new[] { "top", "total", "native", "compare" })
    tests.Case($"{view} rejects zero counts", () =>
    {
        var zero = tests.Write("zero.folded", "root;Frame 0\n");
        var result = view == "compare" ? RpcScriptTests.Run("scripts/PerfReport.cs", view, valid, zero) : RpcScriptTests.Run("scripts/PerfReport.cs", view, zero);
        RpcScriptTests.Assert(result.Code != 0 && result.Error.Contains("no positive sample counts"), result.ToString());
    });
tests.Case("dotTrace retains maximum own time per FQN and escaped generic names", () =>
{
    var path = tests.Write("report.xml", "<Function FQN=\"Generic&lt;T&gt;\" TotalTime=\"100\" OwnTime=\"4\" />\n<Function FQN=\"Generic&lt;T&gt;\" TotalTime=\"90\" OwnTime=\"7\" />\n<Function FQN=\"Other\" TotalTime=\"50\" OwnTime=\"2\" />\n");
    var result = RpcScriptTests.Run("scripts/DotTraceReport.cs", "top", path, "1");
    RpcScriptTests.Assert(result.Code == 0 && result.Output.Contains("Generic&lt;T&gt;") && Regex.IsMatch(result.Output, @"7\s+90") && !result.Output.Contains("Other"), result.ToString());
});
tests.Case("profile comparison keeps distinct punctuation under UTF8 locale", () =>
{
    var colon = "instance void [Nethermind.Evm] Namespace.Type::Method(int32)";
    var spaced = "instance void [Nethermind.Evm] Namespace Type Method(int32)";
    var first = tests.Write("locale-before.folded", $"root;{colon} 50\nroot;{spaced} 50\n");
    var second = tests.Write("locale-after.folded", $"root;{colon} 25\nroot;{spaced} 75\n");
    var previous = Environment.GetEnvironmentVariable("LC_ALL");
    try
    {
        Environment.SetEnvironmentVariable("LC_ALL", "en_US.utf8");
        var result = RpcScriptTests.Run("scripts/PerfReport.cs", "compare", first, second, "2");
        RpcScriptTests.Assert(result.Code == 0 && result.Output.Contains(colon, StringComparison.Ordinal) && result.Output.Contains(spaced, StringComparison.Ordinal), result.ToString());
    }
    finally { Environment.SetEnvironmentVariable("LC_ALL", previous); }
});
tests.Case("dotTrace comparison streams repeated large report rows", () =>
{
    var first = tests.Write("large-before.xml", ""); var second = tests.Write("large-after.xml", "");
    using (var a = File.CreateText(first))
    using (var b = File.CreateText(second))
        for (var index = 0; index < 100_000; index++)
        {
            a.WriteLine("<Function FQN=\"Repeated\" TotalTime=\"100\" OwnTime=\"10\" />");
            b.WriteLine("<Function FQN=\"Repeated\" TotalTime=\"100\" OwnTime=\"15\" />");
        }
    var result = RpcScriptTests.Run("scripts/DotTraceReport.cs", "compare", first, second, "1");
    RpcScriptTests.Assert(result.Code == 0 && result.Output.Contains("+50.0%", StringComparison.Ordinal) && Regex.Matches(result.Output, "Repeated").Count == 2, result.ToString());
});
tests.Complete();
