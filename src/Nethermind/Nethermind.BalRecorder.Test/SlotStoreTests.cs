// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.BalRecorder.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class EraFlatStoreTests
{
    private static string TempDir([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, $"eraflatstore_test_{name}_{System.Threading.Thread.CurrentThread.ManagedThreadId}");

    private static byte[]? TryReadBytes(SlotStore store, long blockNumber)
    {
        byte[]? result = null;
        store.TryRead(blockNumber, (data, _) => result = data.ToArray(), 0);
        return result;
    }

    [Test]
    public void WriteAndRead_RoundTrip()
    {
        string dir = TempDir();
        try
        {
            using SlotStore store = new(dir);
            byte[] payload = [1, 2, 3, 4, 5];

            store.Write(100, payload);

            TryReadBytes(store, 100).Should().Equal(payload);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void TryRead_ReturnsFalse_WhenFileDoesNotExist()
    {
        using SlotStore store = new(TempDir());
        bool found = store.TryRead(999, static (_, _) => { }, 0);
        found.Should().BeFalse();
    }

    [Test]
    public void TryRead_ReturnsFalse_WhenSlotNotWritten()
    {
        string dir = TempDir();
        try
        {
            using SlotStore store = new(dir);
            store.Write(0, [0xAA]);

            // slot 1 is in the same era file but was never written
            store.TryRead(1, static (_, _) => { }, 0).Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void MultipleSlots_SameEra_AreIndependent()
    {
        string dir = TempDir();
        try
        {
            using SlotStore store = new(dir);
            store.Write(0, [0xAA]);
            store.Write(1, [0xBB, 0xCC]);

            TryReadBytes(store, 0).Should().Equal([0xAA]);
            TryReadBytes(store, 1).Should().Equal([0xBB, 0xCC]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void DifferentEras_UsesSeparateFiles()
    {
        string dir = TempDir();
        try
        {
            using SlotStore store = new(dir);
            store.Write(0, [0xAA]);
            store.Write(8192, [0xBB]);

            Directory.GetFiles(dir, "*.bin").Length.Should().Be(2);

            TryReadBytes(store, 0).Should().Equal([0xAA]);
            TryReadBytes(store, 8192).Should().Equal([0xBB]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Write_OverwritesExistingSlot()
    {
        string dir = TempDir();
        try
        {
            using SlotStore store = new(dir);
            store.Write(42, [0xAA]);
            store.Write(42, [0xBB]);

            TryReadBytes(store, 42).Should().Equal([0xBB]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void CustomExtension_IsUsed()
    {
        string dir = TempDir();
        try
        {
            using SlotStore store = new(dir, "bal");
            store.Write(0, [0x01]);

            Directory.GetFiles(dir, "*.bal").Should().HaveCount(1);
            Directory.GetFiles(dir, "*.bin").Should().HaveCount(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void ConcurrentWrites_SameEra_DoNotCorrupt()
    {
        string dir = TempDir();
        try
        {
            using SlotStore store = new(dir);

            Parallel.For(0, 64, i =>
            {
                byte[] data = [(byte)i];
                store.Write(i, data);
            });

            for (int i = 0; i < 64; i++)
            {
                TryReadBytes(store, i).Should().Equal([(byte)i], $"slot {i} should be intact");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void ReadAfterDispose_ThroughNewInstance_SeesOldData()
    {
        string dir = TempDir();
        try
        {
            using (SlotStore w = new(dir)) w.Write(5, [0xEE]);
            using SlotStore r = new(dir);
            TryReadBytes(r, 5).Should().Equal([0xEE]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
