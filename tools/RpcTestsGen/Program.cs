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

Option<string[]> clientsOption = new("--client", "-c")
{
    Description = "Client URL(s) to fetch responses from",
    AllowMultipleArgumentsPerToken = true,
    Required = true
};

Option<int> parallelismOption = new("--parallelism", "-p")
{
    Description = "Client request concurrency",
    DefaultValueFactory = static _ => 1
};

Option<string?> includeOption = new("--include")
{
    Description = "Include only request lines containing this string"
};

Option<string?> excludeOption = new("--exclude")
{
    Description = "Exclude request lines containing this string, takes precedence over inclusion"
};

Option<int?> minBlocksOption = new("--min-blocks")
{
    Description = "Minimum number of blocks for eth_getLogs requests to send"
};

Option<int?> maxBlocksOption = new("--max-blocks")
{
    Description = "Maximum number of blocks for eth_getLogs requests to send"
};

Option<int?> minResultLenOption = new("--min-result-len")
{
    Description = "Minimum number of entries in response `result` array to include"
};

RootCommand rootCommand = new("Generates RPC test files from JSONL request files")
{
    requestsOption,
    clientsOption,
    parallelismOption,
    includeOption,
    excludeOption,
    minBlocksOption,
    maxBlocksOption,
    minResultLenOption
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    Executor executor = new(new ExecutionArgs
    {
        Sources = parseResult.GetRequiredValue(requestsOption).Select(FilePos.Parse).ToArray(),
        Clients = parseResult.GetRequiredValue(clientsOption).Select(static s => new Uri(s)).ToArray(),
        Parallelism = parseResult.GetValue(parallelismOption),
        Include = parseResult.GetValue(includeOption),
        Exclude = parseResult.GetValue(excludeOption),
        MinBlocks = parseResult.GetValue(minBlocksOption),
        MaxBlocks = parseResult.GetValue(maxBlocksOption),
        MinResultLen = parseResult.GetValue(minResultLenOption)
    });

    Console.WriteLine("Starting tests generation...");
    string[] result = await executor.RunAsync(ct);

    Console.WriteLine(result.Length == 0
        ? "No tests generated"
        : $"Generated test files: {string.Join(", ", result)}"
    );
});

return await rootCommand.Parse(args).InvokeAsync();
