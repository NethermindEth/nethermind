// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>Arrange and assert steps for tests of replays that run on copies of a block's transactions.</summary>
internal static class ReplayCopies
{
    private const string SubclassName = "subclass";

    /// <summary>
    /// Mines a block with one transaction of a <see cref="Transaction"/> subclass, as plugins keep in blocks, and returns
    /// the instance the block tree caches. Clears <paramref name="replayed"/> afterwards.
    /// </summary>
    public static async Task<Transaction> MineSubclassTransaction(TestRpcBlockchain chain, PrivateKey signer, List<Transaction> replayed)
    {
        Transaction transaction = Build.A.NamedTransaction(SubclassName)
            .WithTo(TestItem.AddressC)
            .WithNonce(chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, signer.Address))
            .SignedAndResolved(chain.EthereumEcdsa, signer)
            .TestObject;
        await chain.AddBlock(transaction);
        Block head = chain.BlockTree.Head!;
        Assert.That(head.Transactions, Has.Length.EqualTo(1), "precondition: the transaction is mined");
        Transaction cached = chain.BlockTree.FindBlock(head.Hash!, BlockTreeLookupOptions.None)!.Transactions[0];
        Assert.That(cached, Is.TypeOf<NamedTransaction>(), "precondition: the block tree caches the subclass");
        lock (replayed) replayed.Clear();
        return cached;
    }

    /// <summary>Asserts that the replay ran on copies of <paramref name="cached"/> that keep the subclass and its state.</summary>
    public static void AssertReplayedCopies(List<Transaction> replayed, Transaction cached) =>
        Assert.That(replayed, Is.Not.Empty.And.All.Matches<Transaction>(t => !ReferenceEquals(t, cached) && t is NamedTransaction { Name: SubclassName }),
            "the replay runs on copies that keep the subclass and its state");
}
