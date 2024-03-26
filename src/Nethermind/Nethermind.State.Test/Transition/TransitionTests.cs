// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    [Test]
    public void TestProperSequenceOfReads()
    {
        var preImageDb = new MemDb();
        preImageDb[Keccak.Compute(TestItem.AddressA.Bytes).Bytes] = TestItem.AddressA.Bytes;
        preImageDb[Keccak.Compute(TestItem.AddressB.Bytes).Bytes] = TestItem.AddressB.Bytes;
        preImageDb[Keccak.Compute(TestItem.AddressC.Bytes).Bytes] = TestItem.AddressC.Bytes;
        preImageDb[Keccak.Compute(TestItem.AddressD.Bytes).Bytes] = TestItem.AddressD.Bytes;
        preImageDb[Keccak.Compute(TestItem.AddressE.Bytes).Bytes] = TestItem.AddressE.Bytes;

        var merkleCodeDb = new MemDb();
        TrieStore trieStore = new(new MemDb(), Logger);
        WorldState provider = new(trieStore, merkleCodeDb, Logger);

        provider.CreateAccount(TestItem.AddressA, 1.Ether());
        provider.CreateAccount(TestItem.AddressB, 2.Ether());
        provider.CreateAccount(TestItem.AddressC, 3.Ether());
        provider.CreateAccount(TestItem.AddressD, 4.Ether());
        provider.CreateAccount(TestItem.AddressE, 5.Ether());

        provider.Commit(Prague.Instance);
        provider.CommitTree(0);

        provider.Set(new StorageCell(TestItem.AddressB, UInt256.One), TestItem.KeccakA.BytesToArray());
        provider.Set(new StorageCell(TestItem.AddressB, (UInt256)2), TestItem.KeccakA.BytesToArray());
        provider.Set(new StorageCell(TestItem.AddressB, (UInt256)200), TestItem.KeccakA.BytesToArray());

        provider.Set(new StorageCell(TestItem.AddressC, UInt256.One), TestItem.KeccakA.BytesToArray());
        provider.Set(new StorageCell(TestItem.AddressC, (UInt256)2), TestItem.KeccakA.BytesToArray());
        provider.Set(new StorageCell(TestItem.AddressC, (UInt256)200), TestItem.KeccakA.BytesToArray());

        provider.Commit(Prague.Instance);
        provider.CommitTree(1);

        var codeA = new byte[246];
        var codeB = new byte[2460];
        // just to discard a few initial bytes
        TestItem.Random.NextBytes(codeA);
        TestItem.Random.NextBytes(codeA);
        TestItem.Random.NextBytes(codeB);
        provider.InsertCode(TestItem.AddressC, codeA, Prague.Instance);
        provider.InsertCode(TestItem.AddressE, codeB, Prague.Instance);
        provider.Commit(Prague.Instance);
        provider.CommitTree(2);

        var verkleCodeDb = new MemDb();
        IVerkleTreeStore verkleStore = VerkleTestUtils.GetVerkleStoreForTest<PersistEveryBlock>(DbMode.MemDb);
        var verkleTree = new VerkleStateTree(verkleStore, Logger);
        var transitionWorldState = new TransitionWorldState(
            new StateReader(trieStore, merkleCodeDb, Logger), provider.StateRoot, verkleTree, verkleCodeDb, preImageDb,
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
        provider.GetBalance(TestItem.AddressE).Should().BeEquivalentTo(5.Ether());

        transitionWorldState.Get(new StorageCell(TestItem.AddressB, (UInt256)2)).ToArray().Should()
            .BeEquivalentTo(TestItem.KeccakA.BytesToArray());

        transitionWorldState.Set(new StorageCell(TestItem.AddressB, (UInt256)2), TestItem.KeccakB.BytesToArray());

        transitionWorldState.Commit(Prague.Instance);
        transitionWorldState.CommitTree(4);

        transitionWorldState.Get(new StorageCell(TestItem.AddressB, (UInt256)2)).ToArray().Should()
            .BeEquivalentTo(TestItem.KeccakB.BytesToArray());
        provider.Get(new StorageCell(TestItem.AddressB, (UInt256)2)).ToArray().Should()
            .BeEquivalentTo(TestItem.KeccakA.BytesToArray());
    }
}
