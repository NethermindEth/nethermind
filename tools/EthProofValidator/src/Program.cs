// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EthProofValidator;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out int latestBlockId) || !int.TryParse(args[1], out int blockCount))
        {
            Console.WriteLine("Usage: dotnet run <LatestBlockId> <BlockCount>");
            Console.WriteLine("Example: dotnet run 24182425 25");
            return;
        }

        BlockValidator validator = new BlockValidator();

        while (blockCount-- > 0)
        {
            int blockId = latestBlockId - blockCount;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await validator.ValidateBlockAsync(blockId);
            sw.Stop();
            Console.WriteLine($"⏱️  Total Time: {sw.ElapsedMilliseconds} ms");
        }
    }
}
