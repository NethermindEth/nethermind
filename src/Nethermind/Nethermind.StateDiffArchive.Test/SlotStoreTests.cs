// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Extensions;
using Nethermind.StateDiffArchive.Storage;
using NUnit.Framework;

namespace Nethermind.StateDiffArchive.Test;

[Parallelizable(ParallelScope.Self)]
public class SlotStoreTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), $"sds-slot-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static byte[]? Read(SlotStore store, ulong block)
    {
        byte[]? result = null;
        store.TryRead(block, (data, _) => result = data.ToArray(), 0);
        return result;
    }

    [Test]
    public void Writes_and_reads_across_era_boundaries()
    {
        // Blocks chosen to span three era files (8192 blocks each): eras 0, 1, 2.
        (ulong block, byte[] data)[] entries =
        [
            (0, Bytes.FromHexString("0x00")),
            (1, Bytes.FromHexString("0x1122")),
            (SlotFile.SlotsPerFile - 1, Bytes.FromHexString("0xaabbcc")),
            (SlotFile.SlotsPerFile, Bytes.FromHexString("0xdd")),
            (2 * SlotFile.SlotsPerFile + 3, Bytes.FromHexString("0xeeff00112233")),
        ];

        using (SlotStore store = new(_dir, "diff"))
        {
            foreach ((ulong block, byte[] data) in entries)
                Assert.That(store.Write(block, data), Is.True, $"write block {block}");
        }

        using SlotStore reader = new(_dir, "diff");
        Assert.Multiple(() =>
        {
            foreach ((ulong block, byte[] data) in entries)
                Assert.That(Read(reader, block), Is.EqualTo(data), $"read block {block}");

            Assert.That(reader.TryRead(5, static (_, _) => { }, 0), Is.False, "unwritten slot");
            Assert.That(reader.TryRead(100_000, static (_, _) => { }, 0), Is.False, "missing era file");
            Assert.That(Directory.GetFiles(_dir, "*.diff"), Has.Length.EqualTo(3));
        });
    }

    [Test]
    public void Write_respects_overwrite_flag()
    {
        using SlotStore store = new(_dir, "diff");
        byte[] first = Bytes.FromHexString("0x01");
        byte[] second = Bytes.FromHexString("0x0203");

        Assert.That(store.Write(7, first), Is.True);
        Assert.That(store.Write(7, second), Is.False, "occupied slot without overwrite is a no-op");
        Assert.That(Read(store, 7), Is.EqualTo(first));

        Assert.That(store.Write(7, second, allowOverwrite: true), Is.True);
        Assert.That(Read(store, 7), Is.EqualTo(second));
    }
}
