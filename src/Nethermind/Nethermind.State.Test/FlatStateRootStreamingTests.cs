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
using Nethermind.State.Flat;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// A flat scope applies each committed round to its tries on dedicated threads while the block executes. Its roots
/// must not depend on how far that got when the block ended, so every block is checked against the trie backend,
/// which applies the block's changes only at its end.
/// </summary>
[TestFixture]
public class FlatStateRootStreamingTests
{
    private const int BlockCount = 8;
    private const int TransactionsPerBlock = 60;
    private const int ContractCount = 10;
    private const int FirstEoa = 10;
    private const int EoaCount = 20;
    private const int FirstUntouched = 30;
    private const int UntouchedCount = 30;
    private const int SlotRange = 300;

    private static readonly IReleaseSpec Spec = Cancun.Instance;

    [TestCase(1, TestName = "StateRoot_StreamedBlocks_MatchesTrieBackend_Seed1")]
    [TestCase(2, TestName = "StateRoot_StreamedBlocks_MatchesTrieBackend_Seed2")]
    [TestCase(3, TestName = "StateRoot_StreamedBlocks_MatchesTrieBackend_Seed3")]
    [TestCase(4, TestName = "StateRoot_StreamedBlocks_MatchesTrieBackend_Seed4")]
    [TestCase(5, TestName = "StateRoot_StreamedBlocks_MatchesTrieBackend_Seed5")]
    [TestCase(6, TestName = "StateRoot_StreamedBlocks_MatchesTrieBackend_Seed6")]
    [TestCase(7, TestName = "StateRoot_StreamedBlocks_MatchesTrieBackend_Seed7")]
    [TestCase(8, TestName = "StateRoot_StreamedBlocks_MatchesTrieBackend_Seed8")]
    public void StateRoot_StreamedBlocks_MatchesTrieBackend(int seed)
    {
        List<List<Transaction>> blocks = GenerateBlocks(new Random(seed));

        Hash256[] expected = Execute(TestWorldStateFactory.CreateForTest(), blocks, pauses: false);

        long mismatchesBefore = Metrics.StateRootStreamMismatches;
        long fallbacksBefore = Metrics.StateRootStreamFallbacks;
        (IWorldState flatState, _, IContainer container) = TestWorldStateFactory.CreateFlatForTestWithStateReader();
        Hash256[] actual;
        using (container)
        {
            actual = Execute(flatState, blocks, pauses: true);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected), "every block's root must be the one the trie backend computes from the same changes");
            Assert.That(Metrics.StateRootStreamMismatches, Is.EqualTo(mismatchesBefore), "the streamed root must equal the written one on its own, not only after the write batch re-applies the block");
            Assert.That(Metrics.StateRootStreamFallbacks, Is.EqualTo(fallbacksBefore), "streaming must not have fallen back");
        }
    }

    private static Hash256[] Execute(IWorldState worldState, List<List<Transaction>> blocks, bool pauses)
    {
        Hash256[] roots = new Hash256[blocks.Count + 1];
        BlockHeader parent;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            for (int i = 0; i < ContractCount; i++) worldState.CreateAccount(Address(i), 1);
            worldState.Commit(Spec);
            worldState.CommitTree(0);
            roots[0] = worldState.StateRoot;
            parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(roots[0]).TestObject;
        }

        for (int number = 1; number <= blocks.Count; number++)
        {
            using (worldState.BeginScope(parent))
            {
                foreach (Transaction transaction in blocks[number - 1])
                {
                    foreach (Operation operation in transaction.Operations) Apply(worldState, operation);

                    worldState.Commit(Spec, commitRoots: false);
                    if (pauses) Pause(transaction.Pause);
                }

                worldState.Commit(Spec);
                worldState.CommitTree(number);
                roots[number] = worldState.StateRoot;
                parent = Build.A.BlockHeader.WithNumber(number).WithStateRoot(roots[number]).TestObject;
            }
        }

        return roots;
    }

    private static void Apply(IWorldState worldState, Operation operation)
    {
        Address address = Address(operation.Target);
        switch (operation.Kind)
        {
            case OperationKind.Slot:
                worldState.CreateAccountIfNotExists(address, 1);
                worldState.Set(new StorageCell(address, (UInt256)operation.Slot), operation.Value);
                break;
            case OperationKind.Clear:
                worldState.ClearStorage(address);
                break;
            case OperationKind.Credit:
                worldState.AddToBalanceAndCreateIfNotExists(address, operation.Value, Spec);
                break;
            case OperationKind.Debit:
                worldState.SubtractFromBalance(address, operation.Value, Spec);
                break;
            case OperationKind.Nonce:
                worldState.CreateAccountIfNotExists(address, 0);
                worldState.IncrementNonce(address, 1);
                break;
            case OperationKind.Destroy:
                if (!worldState.AccountExists(address)) break;
                worldState.MarkStorageDestroyed(address);
                worldState.DeleteAccount(address);
                break;
        }
    }

    // A yield lets the streamer drain; a sleep lets a hash round in.
    private static void Pause(PauseKind pause)
    {
        switch (pause)
        {
            case PauseKind.Yield:
                Thread.Sleep(0);
                break;
            case PauseKind.Sleep:
                Thread.Sleep(2);
                break;
        }
    }

    // Slots come from a small range so several transactions of a block write them, and some writes and debits put a
    // value back to where the block started, which the end-of-block flush skips as unchanged.
    private static List<List<Transaction>> GenerateBlocks(Random random)
    {
        Dictionary<(int Contract, int Slot), UInt256> slots = [];
        List<List<Transaction>> blocks = new(BlockCount);
        for (int b = 0; b < BlockCount; b++)
        {
            Dictionary<(int Contract, int Slot), UInt256> blockStart = new(slots);
            List<(int Eoa, UInt256 Amount)> credits = [];
            List<Transaction> transactions = new(TransactionsPerBlock);
            for (int t = 0; t < TransactionsPerBlock; t++)
            {
                List<Operation> operations = [];
                int steps = random.Next(1, 5);
                for (int s = 0; s < steps; s++)
                {
                    switch (random.Next(12))
                    {
                        case < 6:
                            AddSlotWrites(random, operations, slots, blockStart);
                            break;
                        case 6:
                            {
                                int contract = random.Next(ContractCount);
                                operations.Add(new Operation(OperationKind.Clear, contract, 0, UInt256.Zero));
                                DropSlots(slots, contract);
                                break;
                            }
                        case 7:
                            {
                                int eoa = FirstEoa + random.Next(EoaCount);
                                UInt256 amount = (UInt256)(ulong)random.Next(1, 1_000_000);
                                operations.Add(new Operation(OperationKind.Credit, eoa, 0, amount));
                                credits.Add((eoa, amount));
                                break;
                            }
                        case 8 when credits.Count > 0:
                            {
                                int index = random.Next(credits.Count);
                                (int eoa, UInt256 amount) = credits[index];
                                credits.RemoveAt(index);
                                operations.Add(new Operation(OperationKind.Debit, eoa, 0, amount));
                                break;
                            }
                        case 9:
                            operations.Add(new Operation(OperationKind.Nonce, FirstEoa + random.Next(EoaCount), 0, UInt256.Zero));
                            break;
                        case 10 when random.Next(3) == 0:
                            {
                                int contract = random.Next(ContractCount);
                                operations.Add(new Operation(OperationKind.Destroy, contract, 0, UInt256.Zero));
                                DropSlots(slots, contract);
                                break;
                            }
                        default:
                            operations.Add(new Operation(OperationKind.Credit, FirstUntouched + random.Next(UntouchedCount), 0, UInt256.Zero));
                            break;
                    }
                }

                PauseKind pause = random.Next(8) switch
                {
                    0 => PauseKind.Sleep,
                    1 or 2 => PauseKind.Yield,
                    _ => PauseKind.None,
                };
                transactions.Add(new Transaction(operations, pause));
            }

            blocks.Add(transactions);
        }

        return blocks;
    }

    private static void AddSlotWrites(
        Random random,
        List<Operation> operations,
        Dictionary<(int Contract, int Slot), UInt256> slots,
        Dictionary<(int Contract, int Slot), UInt256> blockStart)
    {
        int contract = random.Next(ContractCount);
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
            operations.Add(new Operation(OperationKind.Slot, contract, slot, value));
            if (value.IsZero) slots.Remove((contract, slot));
            else slots[(contract, slot)] = value;
        }
    }

    private static void DropSlots(Dictionary<(int Contract, int Slot), UInt256> slots, int contract)
    {
        foreach ((int Contract, int Slot) key in new List<(int, int)>(slots.Keys))
        {
            if (key.Contract == contract) slots.Remove(key);
        }
    }

    private static Address Address(int index) => TestItem.Addresses[index];

    private enum OperationKind
    {
        Slot,
        Clear,
        Credit,
        Debit,
        Nonce,
        Destroy,
    }

    private enum PauseKind
    {
        None,
        Yield,
        Sleep,
    }

    private readonly record struct Operation(OperationKind Kind, int Target, int Slot, UInt256 Value);

    private sealed record Transaction(List<Operation> Operations, PauseKind Pause);
}
