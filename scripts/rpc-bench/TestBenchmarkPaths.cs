#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include BenchmarkProcess.Helpers.cs
#:include BenchmarkPaths.Helpers.cs
#:include RpcScriptTests.Helpers.cs

using var tests = new RpcScriptTests();
foreach (var path in new[] { "", "/", "/data", "relative/path", "/data/../root", "/data/a..b" })
    tests.Case($"reject unsafe deletion root '{path}'", () =>
    {
        try { BenchmarkPaths.AssertSane(path, "root"); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("unsafe root accepted");
    });
tests.Case("valid root with spaces", () => BenchmarkPaths.AssertSane("/data/run space", "root"));
tests.Case("mount matching respects path boundaries and unescapes spaces", () =>
{
    var mounts = tests.Write("mounts", "overlay /data/run overlay rw 0 0\noverlay /data/run/child overlay rw 0 0\noverlay /data/runner overlay rw 0 0\noverlay /data/run/with\\040space overlay rw 0 0\n");
    var values = BenchmarkPaths.MountsUnder("/data/run", mounts);
    RpcScriptTests.Assert(values.SequenceEqual(["/data/run/with space", "/data/run/child", "/data/run"]), string.Join(',', values));
});
tests.Case("equal roots and descendants overlap but sibling prefixes do not", () =>
{
    RpcScriptTests.Assert(BenchmarkPaths.Contains("/data/run", "/data/run") && BenchmarkPaths.Contains("/data/run/", "/data/run/child") && !BenchmarkPaths.Contains("/data/run", "/data/runner"), "path boundary changed");
});
tests.Complete();
