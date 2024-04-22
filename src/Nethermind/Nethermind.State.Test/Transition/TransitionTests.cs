// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Transition;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.Test;
using Nethermind.Verkle.Tree.TreeStore;
using NUnit.Framework;

namespace Nethermind.Store.Test.Transition;

public class TransitionTests
{
    private static readonly ILogManager Logger = LimboLogs.Instance;
    private MemDb _preImageDb;

    private StateReader _merkleReader;
    private WorldState _merkleState;

    [SetUp]
    public void SetUp()
    {
        _preImageDb = new MemDb();
        _preImageDb[Keccak.Compute(TestItem.AddressA.Bytes).Bytes] = TestItem.AddressA.Bytes;
        _preImageDb[Keccak.Compute(TestItem.AddressB.Bytes).Bytes] = TestItem.AddressB.Bytes;
        _preImageDb[Keccak.Compute(TestItem.AddressC.Bytes).Bytes] = TestItem.AddressC.Bytes;
        _preImageDb[Keccak.Compute(TestItem.AddressD.Bytes).Bytes] = TestItem.AddressD.Bytes;
        _preImageDb[Keccak.Compute(TestItem.AddressE.Bytes).Bytes] = TestItem.AddressE.Bytes;
        _preImageDb[Keccak.Compute(UInt256.One.ToBigEndian()).Bytes] = UInt256.One.ToBigEndian();
        _preImageDb[Keccak.Compute(((UInt256)2).ToBigEndian()).Bytes] = ((UInt256)2).ToBigEndian();
        _preImageDb[Keccak.Compute(((UInt256)200).ToBigEndian()).Bytes] = ((UInt256)200).ToBigEndian();

        var merkleCodeDb = new MemDb();
        TrieStore merkleStore = new(new MemDb(), Logger);
        _merkleReader = new(merkleStore, merkleCodeDb, Logger);
        _merkleState = new(merkleStore, merkleCodeDb, Logger);
    }

    [TearDown]
    public void TearDown()
    {
        _preImageDb.Dispose();
    }

    [Test]
    public void TestProperSequenceOfReads()
    {
        _merkleState.CreateAccount(TestItem.AddressA, 1.Ether());
        _merkleState.CreateAccount(TestItem.AddressB, 2.Ether());
        _merkleState.CreateAccount(TestItem.AddressC, 3.Ether());
        _merkleState.CreateAccount(TestItem.AddressD, 4.Ether());
        _merkleState.CreateAccount(TestItem.AddressE, 5.Ether());
        _merkleState.Commit(Cancun.Instance);
        _merkleState.CommitTree(0);

        _merkleState.Set(new StorageCell(TestItem.AddressB, UInt256.One), TestItem.KeccakA.BytesToArray());
        _merkleState.Set(new StorageCell(TestItem.AddressB, (UInt256)2), TestItem.KeccakA.BytesToArray());
        _merkleState.Set(new StorageCell(TestItem.AddressB, (UInt256)200), TestItem.KeccakA.BytesToArray());

        _merkleState.Set(new StorageCell(TestItem.AddressC, UInt256.One), TestItem.KeccakA.BytesToArray());
        _merkleState.Set(new StorageCell(TestItem.AddressC, (UInt256)2), TestItem.KeccakA.BytesToArray());
        _merkleState.Set(new StorageCell(TestItem.AddressC, (UInt256)200), TestItem.KeccakA.BytesToArray());

        _merkleState.Commit(Cancun.Instance);
        _merkleState.CommitTree(1);

        var codeA = new byte[246];
        var codeB = new byte[2460];
        // just to discard a few initial bytes
        TestItem.Random.NextBytes(codeA);
        TestItem.Random.NextBytes(codeA);
        TestItem.Random.NextBytes(codeB);
        _merkleState.InsertCode(TestItem.AddressC, codeA, Prague.Instance);
        _merkleState.InsertCode(TestItem.AddressE, codeB, Prague.Instance);
        _merkleState.Commit(Cancun.Instance);
        _merkleState.CommitTree(2);


        var verkleCodeDb = new MemDb();
        IVerkleTreeStore verkleStore = VerkleTestUtils.GetVerkleStoreForTest<PersistEveryBlock>(DbMode.MemDb);
        var verkleTree = new VerkleStateTree(verkleStore, Logger);
        var transitionWorldState = new TransitionWorldState(
            _merkleReader, _merkleState.StateRoot, verkleTree, verkleCodeDb, _preImageDb,
            Logger);

        transitionWorldState.GetBalance(TestItem.AddressA).Should().BeEquivalentTo(1.Ether());
        transitionWorldState.GetBalance(TestItem.AddressB).Should().BeEquivalentTo(2.Ether());
        transitionWorldState.GetBalance(TestItem.AddressC).Should().BeEquivalentTo(3.Ether());
        transitionWorldState.GetBalance(TestItem.AddressD).Should().BeEquivalentTo(4.Ether());
        transitionWorldState.GetBalance(TestItem.AddressE).Should().BeEquivalentTo(5.Ether());

        transitionWorldState.SubtractFromBalance(TestItem.AddressE, 2.Ether(), Prague.Instance);

        transitionWorldState.Commit(Prague.Instance);
        transitionWorldState.CommitTree(3);

        transitionWorldState.GetBalance(TestItem.AddressE).Should().BeEquivalentTo(3.Ether());
        _merkleState.GetBalance(TestItem.AddressE).Should().BeEquivalentTo(5.Ether());

        transitionWorldState.Get(new StorageCell(TestItem.AddressB, (UInt256)2)).ToArray().Should()
            .BeEquivalentTo(TestItem.KeccakA.BytesToArray());

        transitionWorldState.Set(new StorageCell(TestItem.AddressB, (UInt256)2), TestItem.KeccakB.BytesToArray());

        transitionWorldState.Commit(Prague.Instance);
        transitionWorldState.CommitTree(4);

        transitionWorldState.Get(new StorageCell(TestItem.AddressB, (UInt256)2)).ToArray().Should()
            .BeEquivalentTo(TestItem.KeccakB.BytesToArray());
        _merkleState.Get(new StorageCell(TestItem.AddressB, (UInt256)2)).ToArray().Should()
            .BeEquivalentTo(TestItem.KeccakA.BytesToArray());

        int x = 0;
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
        Console.WriteLine($"this is {x++}");
        transitionWorldState.SweepLeaves(2);
    }
}
