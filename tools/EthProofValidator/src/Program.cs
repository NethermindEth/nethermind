// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.EthProofValidator;

Argument<int> latestBlockIdArg = new("latestBlockId")
{
    Description = "The ID of the latest block to validate",
    HelpName = "id"
};

Argument<int> blockCountArg = new("blockCount")
{
    Description = "The number of blocks to validate",
    HelpName = "count"
};

RootCommand rootCommand =
[
    latestBlockIdArg,
    blockCountArg
];

rootCommand.Description = "Tool to validate Ethereum block via zk proofs";

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    int latestBlockId = parseResult.GetValue(latestBlockIdArg);
    int blockCount = parseResult.GetValue(blockCountArg);

    BlockValidator validator = new();

    while (blockCount-- > 0)
    {
        int blockId = latestBlockId - blockCount;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await validator.ValidateBlockAsync(blockId);
        sw.Stop();
        Console.WriteLine($"⏱️  Total Time: {sw.ElapsedMilliseconds} ms");
    }
});

return await rootCommand.Parse(args).InvokeAsync();
