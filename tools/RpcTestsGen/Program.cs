// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using RpcTestsGen;

Option<string[]> requestsOption = new("--requests", "-r")
{
    Description = "Request file(s), use :123 at the end of the file path to specify starting line",
    AllowMultipleArgumentsPerToken = true,
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

Option<string[]> clientsOption = new("--client", "-c")
{
    Description = "Client URL(s) to fetch responses from",
    AllowMultipleArgumentsPerToken = true,
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
    FileLocation[] requestFiles = parseResult.GetRequiredValue(requestsOption).Select(FileLocation.Parse).ToArray();
    Uri[] clientUrls = parseResult.GetRequiredValue(clientsOption).Select(static s => new Uri(s)).ToArray();

    string? include = parseResult.GetValue(includeOption);
    string? exclude = parseResult.GetValue(excludeOption);
    int parallelism = parseResult.GetValue(parallelismOption);

    Executor executor = new(requestFiles, clientUrls, parallelism, include, exclude);

    Console.WriteLine("Starting tests generation...");
    string[] result = await executor.RunAsync(ct);
    Console.WriteLine($"Generated test files: {string.Join(", ", result)}");
});

return await rootCommand.Parse(args).InvokeAsync();
