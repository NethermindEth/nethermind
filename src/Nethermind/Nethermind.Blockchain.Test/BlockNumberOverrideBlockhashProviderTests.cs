// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockNumberOverrideBlockhashProviderTests
{
    private const ulong ChainLength = 600;
    private const ulong BaseNumber = 400;

    [Test]
    public async Task Resolves_from_the_base_block_ancestry([Values(-5, 0, 1, 10)] int offset, [Values] bool warmCache, [Values] bool overriddenInPlace)
    {
        BlockTreeBuilder builder = Build.A.BlockTree(Build.A.Block.Genesis.TestObject).OfHeadersOnly.OfChainLength(ChainLength);
        BlockTree tree = builder.TestObject;
        BlockhashProvider inner = new(new BlockhashCache(builder.HeaderStore, LimboLogs.Instance), LimboLogs.Instance);
        BlockNumberOverrideBlockhashProvider provider = new(inner, tree);
        BlockHeader @base = tree.FindHeader(BaseNumber, BlockTreeLookupOptions.None)!;
        BlockHeader call = @base.Clone();
        // As for debug_traceCall at a transaction index, where the header serves the preceding transactions first.
        if (overriddenInPlace) provider.GetBlockhash(call, BaseNumber - 1, Frontier.Instance);
        call.Number = (ulong)((long)BaseNumber + offset);
        if (warmCache) await provider.Prefetch(call, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            for (ulong number = call.Number - BlockhashProvider.MaxDepth - 2; number < call.Number + 2; number++)
            {
                Assert.That(provider.GetBlockhash(call, number, Frontier.Instance), Is.EqualTo(Expected(tree, call.Number, number)), $"BLOCKHASH({number})");
            }
        }
    }

    /// <summary>Geth's <c>GetHashFn</c> anchored on the base block, as <c>debug_traceCall</c> sets it up.</summary>
    private static Hash256? Expected(BlockTree tree, ulong callNumber, ulong number) =>
        number >= callNumber || callNumber - number > BlockhashProvider.MaxDepth ? null
        : number < BaseNumber || number == BaseNumber && callNumber == BaseNumber + 1 ? tree.FindHeader(number, BlockTreeLookupOptions.None)!.Hash
        : null;
}
