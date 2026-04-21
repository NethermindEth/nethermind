// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using FluentAssertions;
using Nethermind.BalRecorder;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.BalRecorder.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class RecordedBalStoreTests
{
    private static string TempDir([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, $"recordedBal_test_{name}_{System.Threading.Thread.CurrentThread.ManagedThreadId}");

    private static BlockAccessList MakeBal(params Address[] addresses)
    {
        BlockAccessList bal = new();
        foreach (Address address in addresses)
            bal.AddAccountRead(address);
        return bal;
    }

    [TearDown]
    public void TearDown()
    {
        // cleanup any leftover dirs from this test (best-effort)
    }

    [Test]
    public void InsertAndGet_RoundTrip()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(dir, new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true });
            BlockAccessList bal = MakeBal(TestItem.AddressA, TestItem.AddressB);
            Block block = Build.A.Block.WithNumber(100).TestObject;

            store.Insert(block, bal);
            BlockAccessList? result = store.Get(100, block.Hash!);

            result.Should().NotBeNull();
            result!.GetAccountChanges(TestItem.AddressA).Should().NotBeNull();
            result.GetAccountChanges(TestItem.AddressB).Should().NotBeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Get_ReturnsNull_WhenFileDoesNotExist()
    {
        using RecordedBalStore store = new(TempDir(), new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true });
        store.Get(999, TestItem.KeccakA).Should().BeNull();
    }

    [Test]
    public void Get_ReturnsNull_WhenSlotNotWritten()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(dir, new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true });
            Block block1 = Build.A.Block.WithNumber(0).TestObject;
            store.Insert(block1, MakeBal(TestItem.AddressA));

            // block 1 is in the same era file but was never written
            store.Get(1, TestItem.KeccakA).Should().BeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void MultipleBlocks_SameEra_IndependentSlots()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(dir, new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true });
            Block blockA = Build.A.Block.WithNumber(0).TestObject;
            Block blockB = Build.A.Block.WithNumber(1).TestObject;
            BlockAccessList balA = MakeBal(TestItem.AddressA);
            BlockAccessList balB = MakeBal(TestItem.AddressB);

            store.Insert(blockA, balA);
            store.Insert(blockB, balB);

            BlockAccessList? resultA = store.Get(0, blockA.Hash!);
            BlockAccessList? resultB = store.Get(1, blockB.Hash!);

            resultA!.GetAccountChanges(TestItem.AddressA).Should().NotBeNull();
            resultA.GetAccountChanges(TestItem.AddressB).Should().BeNull();
            resultB!.GetAccountChanges(TestItem.AddressB).Should().NotBeNull();
            resultB.GetAccountChanges(TestItem.AddressA).Should().BeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void BlocksInDifferentEras_UsesSeparateFiles()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(dir, new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true });
            long era0Block = 0;
            long era1Block = 8192;

            Block block0 = Build.A.Block.WithNumber(era0Block).TestObject;
            Block block1 = Build.A.Block.WithNumber(era1Block).TestObject;

            store.Insert(block0, MakeBal(TestItem.AddressA));
            store.Insert(block1, MakeBal(TestItem.AddressB));

            Directory.GetFiles(dir, "*.bal").Length.Should().Be(2);

            store.Get(era0Block, block0.Hash!)!.GetAccountChanges(TestItem.AddressA).Should().NotBeNull();
            store.Get(era1Block, block1.Hash!)!.GetAccountChanges(TestItem.AddressB).Should().NotBeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Insert_DoesNotOverwrite_ExistingSlot()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(dir, new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true });
            Block block = Build.A.Block.WithNumber(42).TestObject;

            store.Insert(block, MakeBal(TestItem.AddressA));
            store.Insert(block, MakeBal(TestItem.AddressB)); // no-op

            BlockAccessList? result = store.Get(42, block.Hash!);
            result!.GetAccountChanges(TestItem.AddressA).Should().NotBeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Get_IgnoresBlockHash()
    {
        string dir = TempDir();
        try
        {
            using RecordedBalStore store = new(dir, new BalRecorderConfig { ReplayEnabled = true, RecordingEnabled = true });
            Block block = Build.A.Block.WithNumber(7).TestObject;
            store.Insert(block, MakeBal(TestItem.AddressA));

            // retrieval with a different hash should still work (era format ignores hash)
            store.Get(7, TestItem.KeccakB).Should().NotBeNull();
        }
        finally { Directory.Delete(dir, true); }
    }
}
