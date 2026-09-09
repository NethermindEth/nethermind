// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BalRecorder.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class RecordedBalStoreTests
{
    private static string TempDir([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, $"recordedBal_test_{name}_{System.Threading.Thread.CurrentThread.ManagedThreadId}");

    private static GeneratedBlockAccessList MakeBal(params Address[] addresses)
    {
        BlockAccessListAtIndex slice = new();
        foreach (Address address in addresses)
            slice.AddAccountRead(address);
        GeneratedBlockAccessList bal = new();
        bal.Merge(slice);
        return bal;
    }

    [Test]
    public void InsertAndGet_RoundTrip()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true, Path = dir }, new InitConfig(), LimboLogs.Instance);
            GeneratedBlockAccessList bal = MakeBal(TestItem.AddressA, TestItem.AddressB);
            Block block = Build.A.Block.WithNumber(100).TestObject;

            store.Insert(block, bal);
            ReadOnlyBlockAccessList? result = store.Get(100);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.GetAccountChanges(TestItem.AddressA), Is.Not.Null);
            Assert.That(result.GetAccountChanges(TestItem.AddressB), Is.Not.Null);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Get_ReturnsNull_WhenFileDoesNotExist()
    {
        using RecordedBalStore store = new(new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true, Path = TempDir() }, new InitConfig(), LimboLogs.Instance);
        Assert.That(store.Get(999), Is.Null);
    }

    [Test]
    public void Get_ReturnsNull_WhenSlotNotWritten()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true, Path = dir }, new InitConfig(), LimboLogs.Instance);
            Block block1 = Build.A.Block.WithNumber(0).TestObject;
            store.Insert(block1, MakeBal(TestItem.AddressA));

            // block 1 is in the same era file but was never written
            Assert.That(store.Get(1), Is.Null);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void MultipleBlocks_SameEra_IndependentSlots()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true, Path = dir }, new InitConfig(), LimboLogs.Instance);
            Block blockA = Build.A.Block.WithNumber(0).TestObject;
            Block blockB = Build.A.Block.WithNumber(1).TestObject;
            GeneratedBlockAccessList balA = MakeBal(TestItem.AddressA);
            GeneratedBlockAccessList balB = MakeBal(TestItem.AddressB);

            store.Insert(blockA, balA);
            store.Insert(blockB, balB);

            ReadOnlyBlockAccessList? resultA = store.Get(0);
            ReadOnlyBlockAccessList? resultB = store.Get(1);

            Assert.That(resultA!.GetAccountChanges(TestItem.AddressA), Is.Not.Null);
            Assert.That(resultA.GetAccountChanges(TestItem.AddressB), Is.Null);
            Assert.That(resultB!.GetAccountChanges(TestItem.AddressB), Is.Not.Null);
            Assert.That(resultB.GetAccountChanges(TestItem.AddressA), Is.Null);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void BlocksInDifferentEras_UsesSeparateFiles()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true, Path = dir }, new InitConfig(), LimboLogs.Instance);
            ulong era0Block = 0;
            ulong era1Block = 8192;

            Block block0 = Build.A.Block.WithNumber(era0Block).TestObject;
            Block block1 = Build.A.Block.WithNumber(era1Block).TestObject;

            store.Insert(block0, MakeBal(TestItem.AddressA));
            store.Insert(block1, MakeBal(TestItem.AddressB));

            Assert.That(Directory.GetFiles(dir, "*.bal").Length, Is.EqualTo(2));

            Assert.That(store.Get(era0Block)!.GetAccountChanges(TestItem.AddressA), Is.Not.Null);
            Assert.That(store.Get(era1Block)!.GetAccountChanges(TestItem.AddressB), Is.Not.Null);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Insert_DoesNotOverwrite_ExistingSlot()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true, Path = dir }, new InitConfig(), LimboLogs.Instance);
            Block block = Build.A.Block.WithNumber(42).TestObject;

            store.Insert(block, MakeBal(TestItem.AddressA));
            store.Insert(block, MakeBal(TestItem.AddressB)); // no-op

            ReadOnlyBlockAccessList? result = store.Get(42);
            Assert.That(result!.GetAccountChanges(TestItem.AddressA), Is.Not.Null);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Get_ByBlockNumber()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true, Path = dir }, new InitConfig(), LimboLogs.Instance);
            Block block = Build.A.Block.WithNumber(7).TestObject;
            store.Insert(block, MakeBal(TestItem.AddressA));
            Assert.That(store.Get(7), Is.Not.Null);
        }
        finally { Directory.Delete(dir, true); }
    }
}
