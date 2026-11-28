#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property PublishAot=false
#:include script-process.helpers.cs

using System.IO.Compression;

const string projectPath = "tools/PgoTrim/PgoTrim.csproj";
var root = new DirectoryInfo(Environment.CurrentDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, projectPath)))
    root = root.Parent;
var project = Path.Combine(root?.FullName ?? throw new DirectoryNotFoundException("Run from within the Nethermind checkout."), projectPath);
var temporary = Directory.CreateTempSubdirectory("nethermind-pgo-trim-");
try
{
    var input = Path.Combine(temporary.FullName, "fixture.jit");
    var output = Path.Combine(temporary.FullName, "trimmed.jit.gz");
    File.WriteAllLines(input,
    [
        "*** START PGO Data, max index = 4 ***",
        Record(1, "HotTyped", 100, "System.String"),
        Record(2, "HotNull", 100, "NULL"),
        Record(3, "ColdTyped", 1, "System.String"),
        Record(4, "ColdNull", 1, "NULL"),
        "*** END PGO Data ***"
    ]);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var result = await ScriptProcess.Run("dotnet", ["run", "--project", project, "-c", "Debug", "--", input, output], timeout.Token);
    if (result.Code != 0)
        throw new InvalidOperationException($"PgoTrim exit {result.Code}: {result.Output}{result.Error}");

    using var data = new StreamReader(new GZipStream(File.OpenRead(output), CompressionMode.Decompress));
    var report = await data.ReadToEndAsync(timeout.Token);
    if (!report.Contains("MethodName: HotTyped", StringComparison.Ordinal) || !report.Contains("MethodName: HotNull", StringComparison.Ordinal) ||
        report.Contains("MethodName: Cold", StringComparison.Ordinal) || !report.Contains("TypeHandle: System.String", StringComparison.Ordinal) ||
        !report.Contains("TypeHandle: NULL", StringComparison.Ordinal) || report.AsSpan().Count("Schema InstrumentationKind 195 ") != 2)
        throw new InvalidOperationException($"Trimmed profile has the wrong records:\n{report}");
    if (!result.Output.Contains("Histograms: 2 total (1 with types, 1 all-NULL)", StringComparison.Ordinal))
        throw new InvalidOperationException($"Summary did not count only emitted histograms:\n{result.Output}");
    Console.WriteLine("PASS summary_counts_only_emitted_histograms");
    Console.WriteLine("Passed 1 tests.");
}
finally
{
    temporary.Delete(recursive: true);
}

static string Record(int hash, string name, int hits, string type) => $"""
    @@@ codehash 0x{hash:X8} methodhash 0x{hash:X8} ilSize 0x00000002 records 0x00000003
    MethodName: {name}
    Signature: void ()
    Schema InstrumentationKind 385 ILOffset 0 Count 1 Other 0
    {hits}
    Schema InstrumentationKind 177 ILOffset 1 Count 1 Other 0
    1
    Schema InstrumentationKind 195 ILOffset 1 Count 1 Other 0
    TypeHandle: {type}
    """;
