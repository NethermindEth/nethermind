// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace EthProofValidator.src
{
    class Program
    {
        static async Task Main()
        {
            var validator = new BlockValidator();
            // await validator.InitializeAsync();

            // The following include Single-GPU (1:100) zk proofs
            // var blockIds = new List<long> { 24046700, 24046800, 24046900, 24047000, 24047100, 24047200, 24047300, 24047400, 24047500, 24047600, 24047700 };

            const long LatestBlockId = 24080984;
            const int BlockCount = 25;
            var blockIds = Enumerable.Range((int)(LatestBlockId - BlockCount), BlockCount+1).Select(i => i).ToList();

            foreach (var blockId in blockIds)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await validator.ValidateBlockAsync(blockId);
                sw.Stop();
                Console.WriteLine($"⏱️  Total Time: {sw.ElapsedMilliseconds} ms");
            }
        }
    }
}