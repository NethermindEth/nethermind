// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;
using NUnit.Framework;

namespace Nethermind.Verkle.Tree.Test;

public class HistoryTests
{

    [TearDown]
    public void CleanTestData()
    {
        string dbPath = VerkleTestUtils.GetDbPathForTest();
        if (Directory.Exists(dbPath))
        {
            Directory.Delete(dbPath, true);
        }
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertGetMultiBlockReverseState(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest(dbMode);

        tree.Insert(VerkleTestUtils._keyVersion, VerkleTestUtils._emptyArray);
        tree.Insert(VerkleTestUtils._keyBalance, VerkleTestUtils._emptyArray);
        tree.Insert(VerkleTestUtils._keyNonce, VerkleTestUtils._emptyArray);
        tree.Insert(VerkleTestUtils._keyCodeCommitment, VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils._keyCodeSize, VerkleTestUtils._emptyArray);
        tree.Commit();
        tree.CommitTree(0);
        VerkleCommitment stateRoot0 = tree.StateRoot;
        Console.WriteLine(tree.StateRoot.ToString());

        tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);

        tree.Insert(VerkleTestUtils._keyVersion, VerkleTestUtils._arrayAll0Last2);
        tree.Insert(VerkleTestUtils._keyBalance, VerkleTestUtils._arrayAll0Last2);
        tree.Insert(VerkleTestUtils._keyNonce, VerkleTestUtils._arrayAll0Last2);
        tree.Insert(VerkleTestUtils._keyCodeCommitment, VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils._keyCodeSize, VerkleTestUtils._arrayAll0Last2);
        tree.Commit();
        tree.CommitTree(1);
        VerkleCommitment stateRoot1 = tree.StateRoot;
        Console.WriteLine(tree.StateRoot.ToString());

        tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);

        tree.Insert(VerkleTestUtils._keyVersion, VerkleTestUtils._arrayAll0Last3);
        tree.Insert(VerkleTestUtils._keyBalance, VerkleTestUtils._arrayAll0Last3);
        tree.Insert(VerkleTestUtils._keyNonce, VerkleTestUtils._arrayAll0Last3);
        tree.Insert(VerkleTestUtils._keyCodeCommitment, VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils._keyCodeSize, VerkleTestUtils._arrayAll0Last3);
        tree.Commit();
        tree.CommitTree(2);
        Console.WriteLine(tree.StateRoot.ToString());

        tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last3);
        tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last3);
        tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last3);
        tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last3);

        tree.StateRoot = stateRoot1;

        tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);

        tree.StateRoot = stateRoot0;

        tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertGetBatchMultiBlockReverseState(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest(dbMode);

        tree.Insert(VerkleTestUtils._keyVersion, VerkleTestUtils._emptyArray);
        tree.Insert(VerkleTestUtils._keyBalance, VerkleTestUtils._emptyArray);
        tree.Insert(VerkleTestUtils._keyNonce, VerkleTestUtils._emptyArray);
        tree.Insert(VerkleTestUtils._keyCodeCommitment, VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils._keyCodeSize, VerkleTestUtils._emptyArray);
        tree.Commit();
        tree.CommitTree(0);
        VerkleCommitment stateRoot0 = tree.StateRoot;

        tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);

        tree.Insert(VerkleTestUtils._keyVersion, VerkleTestUtils._arrayAll0Last2);
        tree.Insert(VerkleTestUtils._keyBalance, VerkleTestUtils._arrayAll0Last2);
        tree.Insert(VerkleTestUtils._keyNonce, VerkleTestUtils._arrayAll0Last2);
        tree.Insert(VerkleTestUtils._keyCodeCommitment, VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils._keyCodeSize, VerkleTestUtils._arrayAll0Last2);
        tree.Commit();
        tree.CommitTree(1);

        tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);

        tree.Insert(VerkleTestUtils._keyVersion, VerkleTestUtils._arrayAll0Last3);
        tree.Insert(VerkleTestUtils._keyBalance, VerkleTestUtils._arrayAll0Last3);
        tree.Insert(VerkleTestUtils._keyNonce, VerkleTestUtils._arrayAll0Last3);
        tree.Insert(VerkleTestUtils._keyCodeCommitment, VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils._keyCodeSize, VerkleTestUtils._arrayAll0Last3);
        tree.Commit();
        tree.CommitTree(2);

        tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last3);
        tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last3);
        tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last3);
        tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last3);

        tree.StateRoot = stateRoot0;

        tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
        tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._emptyArray);
    }


    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestReverseDiffThenForwardDiff(DbMode dbMode)
    {
        // VerkleTree tree = VerkleTestUtils.GetFilledVerkleTreeForTest(dbMode);
        //
        // VerkleMemoryDb memory = tree.GetReverseMergedDiff(3, 1);
        //
        // tree.ApplyDiffLayer(memory, 3, 1);
        //
        // tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        // tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        // tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        // tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        // tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last2);
        //
        // VerkleMemoryDb forwardMemory = tree.GetForwardMergedDiff(1, 3);
        //
        // tree.ApplyDiffLayer(forwardMemory, 1, 3);
        //
        // tree.Get(VerkleTestUtils._keyVersion).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last4);
        // tree.Get(VerkleTestUtils._keyBalance).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last4);
        // tree.Get(VerkleTestUtils._keyNonce).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last4);
        // tree.Get(VerkleTestUtils._keyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils._valueEmptyCodeHashValue);
        // tree.Get(VerkleTestUtils._keyCodeSize).Should().BeEquivalentTo(VerkleTestUtils._arrayAll0Last4);

    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestReverseStateOneBlock(DbMode dbMode)
    {
        // VerkleTree tree = VerkleTestUtils.GetFilledVerkleTreeForTest(dbMode);
        // DateTime start = DateTime.Now;
        // tree.ReverseState();
        // DateTime end = DateTime.Now;
        // Console.WriteLine($"ReverseState() 1 Block: {(end - start).TotalMilliseconds}");
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestForwardStateOneBlock(DbMode dbMode)
    {
        // VerkleTree tree = VerkleTestUtils.GetFilledVerkleTreeForTest(dbMode);
        // tree.ReverseState();
        // VerkleMemoryDb forwardMemory = tree.GetForwardMergedDiff(2, 3);
        // DateTime start = DateTime.Now;
        // tree.ApplyDiffLayer(forwardMemory, 2, 3);
        // DateTime end = DateTime.Now;
        // Console.WriteLine($"ForwardState() 1 Block Insert: {(end - start).TotalMilliseconds}");
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestBatchReverseDiffs(DbMode dbMode)
    {
        // VerkleTree tree = GetHugeVerkleTreeForTest(dbMode);
        // for (int i = 2;i <= 1000;i++) {
        //     DateTime start = DateTime.Now;
        //     IVerkleDiffDb reverseDiff = tree.GetReverseMergedDiff(1, i);
        //     DateTime check1 = DateTime.Now;
        //     tree.ReverseState(reverseDiff, (i -1));
        //     DateTime check2 = DateTime.Now;
        //     Console.WriteLine($"Batch Reverse Diff Fetch(1, {i}): {(check1 - start).TotalMilliseconds}");
        //     Console.WriteLine($"Batch Reverse State(2, {i-1}): {(check2 - check1).TotalMilliseconds}");
        //}
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestBatchForwardDiffs(DbMode dbMode)
    {
        // VerkleTree tree = GetHugeVerkleTreeForTest(dbMode);
        // for (int i = 2;i <= 1000;i++) {
        //     DateTime start = DateTime.Now;
        //     IVerkleDiffDb forwardDiff = tree.GetForwardMergedDiff(1, i);
        //     DateTime check1 = DateTime.Now;
        //     tree.ForwardState(reverseDiff, (i -1));
        //     DateTime check2 = DateTime.Now;
        //     Console.WriteLine($"Batch Forward Diff Fetch(1, {i}): {(check1 - start).TotalMilliseconds}");
        //     Console.WriteLine($"Batch Forward State(2, {i-1}): {(check2 - check1).TotalMilliseconds}");
        //}
    }


}
