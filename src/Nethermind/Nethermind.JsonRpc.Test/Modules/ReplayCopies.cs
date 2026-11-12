// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>Arrange and assert steps for tests of replays that run on copies of a block's transactions.</summary>
internal static class ReplayCopies
{
    private const string SubclassName = "subclass";

    /// <summary>Gas values no execution writes, put on a block tree's transaction so that a replay writing into it
    /// shows even when it writes the value the block's processing wrote.</summary>
    private const ulong SpentGasSentinel = 1_234_567;
    private const ulong BlockGasUsedSentinel = 7_654_321;

    /// <summary>The block tree's instance of a transaction and the values a replay must leave on it.</summary>
    public sealed record Stored(Transaction Instance, ulong Nonce, Address? Sender, Hash256 Hash)
    {
        /// <summary>Puts the gas sentinels on <paramref name="instance"/> and records its values.</summary>
        public static Stored Mark(Transaction instance)
        {
            instance.SpentGas = SpentGasSentinel;
            instance.BlockGasUsed = BlockGasUsedSentinel;
            return new Stored(instance, instance.Nonce, instance.SenderAddress, instance.Hash!);
        }

        /// <summary>Asserts that nothing was written into the instance.</summary>
        public void AssertUnchanged()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Instance.Nonce, Is.EqualTo(Nonce), "the block tree's transaction keeps its nonce");
                Assert.That(Instance.SenderAddress, Is.EqualTo(Sender), "the block tree's transaction keeps its sender");
                Assert.That(Instance.Hash, Is.EqualTo(Hash), "the block tree's transaction keeps its hash");
                Assert.That(Instance.SpentGas, Is.EqualTo(SpentGasSentinel), "the block tree's transaction keeps its spent gas");
                Assert.That(Instance.BlockGasUsed, Is.EqualTo(BlockGasUsedSentinel), "the block tree's transaction keeps its block gas");
            }
        }
    }

    /// <summary>
    /// Mines a block with <paramref name="count"/> transactions of a <see cref="Transaction"/> subclass, as plugins keep in
    /// blocks, and returns the instances the block tree caches, marked with <see cref="Stored.Mark"/>. Clears
    /// <paramref name="replayed"/> afterwards.
    /// </summary>
    public static async Task<Stored[]> MineSubclassTransactions(TestRpcBlockchain chain, PrivateKey signer, List<Transaction> replayed, int count)
    {
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, signer.Address);
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            transactions[i] = Build.A.NamedTransaction(SubclassName)
                .WithTo(TestItem.AddressC)
                .WithNonce(nonce + (ulong)i)
                .SignedAndResolved(chain.EthereumEcdsa, signer)
                .TestObject;
        }

        await chain.AddBlock(transactions);
        Block head = chain.BlockTree.Head!;
        Assert.That(head.Transactions, Has.Length.EqualTo(count), "precondition: the transactions are mined");
        Transaction[] cached = chain.BlockTree.FindBlock(head.Hash!, BlockTreeLookupOptions.None)!.Transactions;
        Assert.That(cached, Has.All.TypeOf<NamedTransaction>(), "precondition: the block tree caches the subclass");
        lock (replayed) replayed.Clear();
        return [.. cached.Select(Stored.Mark)];
    }

    /// <inheritdoc cref="MineSubclassTransactions"/>
    public static async Task<Stored> MineSubclassTransaction(TestRpcBlockchain chain, PrivateKey signer, List<Transaction> replayed) =>
        (await MineSubclassTransactions(chain, signer, replayed, 1))[0];

    /// <summary>
    /// Asserts that the replay executed <paramref name="executions"/> transactions, each a copy of one of
    /// <paramref name="stored"/> with its runtime type, its state and its hash, and that it left every stored instance as
    /// it was.
    /// </summary>
    public static void AssertReplayedCopies(List<Transaction> replayed, int executions, params Stored[] stored)
    {
        Transaction[] executed;
        lock (replayed) executed = [.. replayed];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(executed, Has.Length.EqualTo(executions), "the replay executed the expected transactions");
            foreach (Transaction transaction in executed)
            {
                Stored? source = stored.FirstOrDefault(s => s.Hash == transaction.Hash);
                Assert.That(source, Is.Not.Null, $"the replay executed {transaction.Hash}, which is not one of the block's transactions");
                if (source is null) continue;
                Assert.That(transaction, Is.Not.SameAs(source.Instance), "the replay runs on a copy");
                Assert.That(transaction.GetType(), Is.EqualTo(source.Instance.GetType()), "the copy keeps the runtime type");
                if (source.Instance is NamedTransaction { Name: var name })
                    Assert.That((transaction as NamedTransaction)?.Name, Is.EqualTo(name), "the copy keeps the subclass state");
            }

            foreach (Stored transaction in stored) transaction.AssertUnchanged();
        }
    }
}
