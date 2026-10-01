// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Tracing;

public class TraceCallBlockhashProviderTests
{
    [Test]
    public void Backward_override_walks_ancestors_once([Values] bool deepestFirst)
    {
        IBlockTree tree = Substitute.For<IBlockTree>();
        IBlockhashProvider inner = Substitute.For<IBlockhashProvider>();
        ulong next = 999;
        int reads = 0;
        int branch = 0;
        tree.FindHeader(Arg.Any<Hash256>(), BlockTreeLookupOptions.None).Returns(call =>
        {
            Assert.That(call.Arg<Hash256>(), Is.EqualTo(Hash(next, branch)));
            reads++;
            return Header(next--, branch);
        });
        GethStyleTracer.TraceCallRequestState state = new() { BlockhashLookup = new(Header(1000), 300) };
        TraceCallBlockhashProvider provider = new(inner, tree, state);
        BlockHeader current = Header(300);
        ulong first = deepestFirst ? 44UL : 299UL;
        Assert.That(provider.GetBlockhash(current, first, Prague.Instance), Is.EqualTo(Hash(first)));
        int firstReads = reads;
        Assert.That(provider.GetBlockhash(current, first, Prague.Instance), Is.EqualTo(Hash(first)));
        Assert.That(reads, Is.EqualTo(firstReads));
        for (ulong number = 44; number < 300; number++)
            Assert.That(provider.GetBlockhash(current, number, Prague.Instance), Is.EqualTo(Hash(number)));
        Assert.That(reads, Is.EqualTo(955));
        Assert.That(provider.GetBlockhash(current, 43, Prague.Instance), Is.Null);
        Assert.That(reads, Is.EqualTo(955));

        branch = 1;
        next = 300;
        state.BlockhashLookup = new(Header(301, branch), 300);
        Assert.That(provider.GetBlockhash(current, 299, Prague.Instance), Is.EqualTo(Hash(299, branch)));
        Assert.That(reads, Is.EqualTo(956), "A new call must not reuse the previous call's cached branch.");
    }

    [Test]
    public void Backward_override_checks_cancellation_during_traversal_and_reset_delegates()
    {
        using CancellationTokenSource cancellation = new();
        IBlockTree tree = Substitute.For<IBlockTree>();
        IBlockhashProvider inner = Substitute.For<IBlockhashProvider>();
        int reads = 0;
        tree.FindHeader(Arg.Any<Hash256>(), BlockTreeLookupOptions.None).Returns(_ =>
        {
            if (++reads == 5) cancellation.Cancel();
            return Header(1_000_000UL - (ulong)reads);
        });
        GethStyleTracer.TraceCallRequestState state = new()
        {
            BlockhashLookup = new(Header(1_000_000), 300) { Token = cancellation.Token }
        };
        TraceCallBlockhashProvider provider = new(inner, tree, state);
        BlockHeader current = Header(300);
        Assert.Throws<OperationCanceledException>(() => provider.GetBlockhash(current, 299, Prague.Instance));
        Assert.That(reads, Is.EqualTo(5));

        state.BlockhashLookup = null;
        inner.GetBlockhash(current, 299, Prague.Instance).Returns(TestItem.KeccakA);
        Assert.That(provider.GetBlockhash(current, 299, Prague.Instance), Is.EqualTo(TestItem.KeccakA));
        inner.Received(1).GetBlockhash(current, 299, Prague.Instance);
        Assert.That(reads, Is.EqualTo(5));
    }

    [Test]
    public void Missing_ancestor_is_not_looked_up_repeatedly()
    {
        IBlockTree tree = Substitute.For<IBlockTree>();
        IBlockhashProvider inner = Substitute.For<IBlockhashProvider>();
        GethStyleTracer.TraceCallRequestState state = new() { BlockhashLookup = new(Header(1000), 300) };
        TraceCallBlockhashProvider provider = new(inner, tree, state);
        BlockHeader current = Header(300);
        Assert.That(provider.GetBlockhash(current, 299, Prague.Instance), Is.Null);
        Assert.That(provider.GetBlockhash(current, 44, Prague.Instance), Is.Null);
        tree.Received(1).FindHeader(Arg.Any<Hash256>(), BlockTreeLookupOptions.None);
    }

    private static Hash256 Hash(ulong number, int branch = 0) => new($"0x{branch:x2}{number:x62}");

    private static BlockHeader Header(ulong number, int branch = 0) => Build.A.BlockHeader.WithNumber(number)
        .WithHash(Hash(number, branch)).WithParentHash(Hash(number == 0 ? 0 : number - 1, branch)).TestObject;
}
