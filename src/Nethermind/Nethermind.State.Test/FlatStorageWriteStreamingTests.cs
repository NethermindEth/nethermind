// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// Storage writes reach the flat storage tries on trie warmer jobs while a block executes; the roots must not depend
/// on how far those jobs got, so every block is checked against the trie backend, which applies writes only at the
/// end of the block.
/// </summary>
[TestFixture]
public class FlatStorageWriteStreamingTests
{
    private const int BlockCount = 8;
    private const int TransactionsPerBlock = 60;
    private const int ContractCount = 10;
    private const int SlotRange = 300;

    private static readonly IReleaseSpec Spec = Cancun.Instance;

    [TestCase(1, TestName = "StateRoot_StreamedStorageWrites_MatchesTrieBackend_Seed1")]
    [TestCase(2, TestName = "StateRoot_StreamedStorageWrites_MatchesTrieBackend_Seed2")]
    [TestCase(3, TestName = "StateRoot_StreamedStorageWrites_MatchesTrieBackend_Seed3")]
    [TestCase(4, TestName = "StateRoot_StreamedStorageWrites_MatchesTrieBackend_Seed4")]
    [TestCase(5, TestName = "StateRoot_StreamedStorageWrites_MatchesTrieBackend_Seed5")]
    [TestCase(6, TestName = "StateRoot_StreamedStorageWrites_MatchesTrieBackend_Seed6")]
    public void StateRoot_StreamedStorageWrites_MatchesTrieBackend(int seed)
    {
        List<Block> blocks = GenerateBlocks(new Random(seed));

        Hash256[] expected = Execute(TestWorldStateFactory.CreateForTest(), blocks, pauses: false);

        (IWorldState flatState, _, IContainer container) = TestWorldStateFactory.CreateFlatForTestWithStateReader();
        Hash256[] actual;
        using (container)
        {
            actual = Execute(flatState, blocks, pauses: true);
        }

        Assert.That(actual, Is.EqualTo(expected), "every block's root must be the one the trie backend computes from the same writes");
    }

    private static Hash256[] Execute(IWorldState worldState, List<Block> blocks, bool pauses)
    {
        Hash256[] roots = new Hash256[blocks.Count + 1];
        BlockHeader parent;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            for (int i = 0; i < ContractCount; i++) worldState.CreateAccount(Contract(i), 1);
            worldState.Commit(Spec);
            worldState.CommitTree(0);
            roots[0] = worldState.StateRoot;
            parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(roots[0]).TestObject;
        }

        for (int number = 1; number <= blocks.Count; number++)
        {
            using (worldState.BeginScope(parent))
            {
                foreach (Transaction transaction in blocks[number - 1].Transactions)
                {
                    foreach (Operation operation in transaction.Operations)
                    {
                        Address contract = Contract(operation.Contract);
                        if (operation.Clears) worldState.ClearStorage(contract);
                        else worldState.Set(new StorageCell(contract, (UInt256)operation.Slot), operation.Value);
                    }

                    worldState.Commit(Spec, commitRoots: false);
                    if (pauses && transaction.Pause) Thread.Sleep(0);
                }

                worldState.Commit(Spec);
                worldState.CommitTree(number);
                roots[number] = worldState.StateRoot;
                parent = Build.A.BlockHeader.WithNumber(number).WithStateRoot(roots[number]).TestObject;
            }
        }

        return roots;
    }

    // Slots are drawn from a small range so they are written by several transactions of a block, and some writes put a
    // slot back to its value at the start of the block, which the end-of-block flush skips as unchanged.
    private static List<Block> GenerateBlocks(Random random)
    {
        Dictionary<(int Contract, int Slot), UInt256> current = [];
        List<Block> blocks = new(BlockCount);
        for (int b = 0; b < BlockCount; b++)
        {
            Dictionary<(int Contract, int Slot), UInt256> blockStart = new(current);
            List<Transaction> transactions = new(TransactionsPerBlock);
            for (int t = 0; t < TransactionsPerBlock; t++)
            {
                List<Operation> operations = [];
                int contractsTouched = random.Next(1, 4);
                for (int c = 0; c < contractsTouched; c++)
                {
                    int contract = random.Next(ContractCount);
                    if (random.Next(40) == 0)
                    {
                        operations.Add(new Operation(contract, 0, UInt256.Zero, Clears: true));
                        foreach ((int Contract, int Slot) key in new List<(int, int)>(current.Keys))
                        {
                            if (key.Contract == contract) current.Remove(key);
                        }
                    }

                    int writes = random.Next(20) == 0 ? random.Next(40, 160) : random.Next(1, 6);
                    for (int w = 0; w < writes; w++)
                    {
                        int slot = random.Next(SlotRange);
                        UInt256 value = random.Next(5) switch
                        {
                            0 => UInt256.Zero,
                            1 => blockStart.GetValueOrDefault((contract, slot)),
                            _ => (UInt256)(ulong)random.NextInt64(1, long.MaxValue),
                        };
                        operations.Add(new Operation(contract, slot, value, Clears: false));
                        if (value.IsZero) current.Remove((contract, slot));
                        else current[(contract, slot)] = value;
                    }
                }

                transactions.Add(new Transaction(operations, Pause: random.Next(4) == 0));
            }

            blocks.Add(new Block(transactions));
        }

        return blocks;
    }

    private static Address Contract(int index) => TestItem.Addresses[index];

    private readonly record struct Operation(int Contract, int Slot, UInt256 Value, bool Clears);

    private sealed record Transaction(List<Operation> Operations, bool Pause);

    private sealed record Block(List<Transaction> Transactions);
}
