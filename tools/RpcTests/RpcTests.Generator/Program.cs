// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.RpcTests.Generator;
using SmartFormat;
using SmartFormat.Core.Parsing;

Option<string[]> requestsOption = new("--requests", "-r")
{
    Description = "Request file(s), use :123 at the end of the file path to specify starting line",
    AllowMultipleArgumentsPerToken = true,
    Required = true
};

Option<string?> clientsOption = new("--client", "-c")
{
    Description = "Client URL to fetch responses from"
};

Option<int> parallelismOption = new("--parallelism", "-p")
{
    Description = "Client request concurrency",
    DefaultValueFactory = static _ => 1
};

Option<string[]> methodsOption = new("--methods", "--method", "-m")
{
    Description = "Include only requests with these method names",
    AllowMultipleArgumentsPerToken = true
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

Option<Format> outputPathFormat = new("--out", "-o")
{
    Description = "Output file path, formattable",
    CustomParser = arg => Smart.Default.Parser.ParseFormat(arg.Tokens.Single().Value.Replace(@"\", @"\\")),
    DefaultValueFactory = _ => Smart.Default.Parser.ParseFormat("{FileDir}/{FileName}.test.json")
};

RootCommand rootCommand = new("Generates RPC test files from JSONL request files")
{
    requestsOption,
    clientsOption,
    parallelismOption,
    methodsOption,
    minBlocksOption,
    maxBlocksOption,
    minResultLenOption,
    outputPathFormat
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    ExecutionArgs args = new()
    {
        Sources = [.. parseResult.GetRequiredValue(requestsOption).Select(FilePos.Parse)],
        Client = parseResult.GetValue(clientsOption) is { } c ? new Uri(c) : null,
        Parallelism = parseResult.GetValue(parallelismOption),
        Methods = [.. parseResult.GetValue(methodsOption) ?? []],
        MinBlocks = parseResult.GetValue(minBlocksOption),
        MaxBlocks = parseResult.GetValue(maxBlocksOption),
        MinResultLen = parseResult.GetValue(minResultLenOption),
        OutputPath = parseResult.GetRequiredValue(outputPathFormat)
    };

    Console.WriteLine("Starting tests generation...");
    int outputCount = await TestGenerator.GenerateAsync(args, ct);

    Console.WriteLine(outputCount == 0
        ? "No tests generated"
        : $"Generated {outputCount} test files"
    );
});

return await rootCommand.Parse(args).InvokeAsync();
