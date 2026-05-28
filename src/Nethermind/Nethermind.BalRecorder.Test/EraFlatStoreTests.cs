// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading.Tasks;
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

            Assert.That(TryReadBytes(store, 100), Is.EqualTo(payload));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void TryRead_ReturnsFalse_WhenFileDoesNotExist()
    {
        using SlotStore store = new(TempDir());
        bool found = store.TryRead(999, static (_, _) => { }, 0);
        Assert.That(found, Is.False);
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
            Assert.That(store.TryRead(1, static (_, _) => { }, 0), Is.False);
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

            Assert.That(TryReadBytes(store, 0), Is.EqualTo([0xAA]));
            Assert.That(TryReadBytes(store, 1), Is.EqualTo([0xBB, 0xCC]));
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

            Assert.That(Directory.GetFiles(dir, "*.bin").Length, Is.EqualTo(2));

            Assert.That(TryReadBytes(store, 0), Is.EqualTo([0xAA]));
            Assert.That(TryReadBytes(store, 8192), Is.EqualTo([0xBB]));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Write_DoesNotOverwriteExistingSlot()
    {
        string dir = TempDir();
        try
        {
            using SlotStore store = new(dir);
            store.Write(42, [0xAA]);
            store.Write(42, [0xBB]);

            Assert.That(TryReadBytes(store, 42), Is.EqualTo([0xAA]));
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

            Assert.That((Directory.GetFiles(dir, "*.bal")).Length, Is.EqualTo(1));
            Assert.That((Directory.GetFiles(dir, "*.bin")).Length, Is.EqualTo(0));
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
                Assert.That(TryReadBytes(store, i), Is.EqualTo(new[] { (byte)i }), $"slot {i} should be intact");
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
            Assert.That(TryReadBytes(r, 5), Is.EqualTo([0xEE]));
        }
        finally { Directory.Delete(dir, true); }
    }
}
