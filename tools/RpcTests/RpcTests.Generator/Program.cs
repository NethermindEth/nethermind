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

Option<string> archiveClientOption = new("--client", "-c")
{
    Description = "Archive node URL to source account/slot sets from and capture expected responses",
    Required = true
};

Option<long[]> blocksOption = new("--blocks", "-b")
{
    Description = "Base block number(s) to generate archive-index probe tests for",
    AllowMultipleArgumentsPerToken = true,
    Required = true
};

Option<string> archiveOutOption = new("--out", "-o")
{
    Description = "Output directory; one file per request as archive-index-<baseBlock>-<queryBlock>.test.json",
    DefaultValueFactory = _ => "."
};

Option<int> archiveParallelismOption = new("--parallelism", "-p")
{
    Description = "Client request concurrency",
    DefaultValueFactory = static _ => 1
};

Command archiveCommand = new("archive-index", "Generates archive-index floor-seek probe tests for the given base block(s)")
{
    archiveClientOption,
    blocksOption,
    archiveOutOption,
    archiveParallelismOption
};

archiveCommand.SetAction(async (parseResult, ct) =>
{
    Uri client = new(parseResult.GetRequiredValue(archiveClientOption));
    long[] blocks = parseResult.GetRequiredValue(blocksOption);
    string outDir = parseResult.GetValue(archiveOutOption)!;
    int parallelism = parseResult.GetValue(archiveParallelismOption);

    Console.WriteLine($"Generating archive-index tests for {blocks.Length} block(s)...");
    int outputCount = await TestGenerator.GenerateArchiveIndexAsync(client, blocks, outDir, parallelism, ct);

    Console.WriteLine(outputCount == 0
        ? "No tests generated"
        : $"Generated {outputCount} test files"
    );
});

rootCommand.Subcommands.Add(archiveCommand);

return await rootCommand.Parse(args).InvokeAsync();
