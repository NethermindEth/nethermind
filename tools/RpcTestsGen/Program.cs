// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using RpcTestsGen;

Option<string> requestsOption = new("--requests", "-r")
{
    Description = "Comma-separated list of request files, use :123 at the end of the file path to specify starting line",
    Required = true
};

Option<string?> includeOption = new("--include")
{
    Description = "Include only lines containing this string"
};

Option<string?> excludeOption = new("--exclude")
{
    Description = "Exclude lines containing this string, takes precedence over inclusion"
};

Option<string> clientsOption = new("--clients", "-c")
{
    Description = "Comma-separated list of client URLs",
    Required = true
};

Option<int> parallelismOption = new("--parallelism", "-p")
{
    Description = "Client request concurrency",
    DefaultValueFactory = _ => 1
};

RootCommand rootCommand = new("Generates RPC test files from JSONL request files")
{
    requestsOption,
    includeOption,
    excludeOption,
    clientsOption,
    parallelismOption
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    FileLocation[] requestFiles = parseResult.GetRequiredValue(requestsOption)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(FileLocation.Parse)
        .ToArray();

    string[] clientUrls = parseResult.GetRequiredValue(clientsOption)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    string? include = parseResult.GetValue(includeOption);
    string? exclude = parseResult.GetValue(excludeOption);
    int parallelism = parseResult.GetValue(parallelismOption);

    Executor executor = new(requestFiles, clientUrls, parallelism, include, exclude);

    Console.WriteLine("Starting tests generation...");
    string[] result = await executor.RunAsync(ct);
    Console.WriteLine($"Generated test files: {string.Join(", ", result)}");
});


return await rootCommand.Parse(args).InvokeAsync();
