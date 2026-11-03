// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Store;
using Paprika.Store.PageManagers;

namespace Paprika.Tests.Store;

public class RootFormatTests
{
    private const long Size = 1024 * 1024;
    private const long OneTiB = 1L << 40;
    private const byte HistoryDepth = 2;

    // Page offsets of the root's format block, which starts at payload offset 3632, after the 8-byte page header.
    private const int MagicOffset = PageHeader.Size + 3632;
    private const int VersionOffset = MagicOffset + sizeof(uint);
    private const int HistoryDepthOffset = VersionOffset + sizeof(ushort);
    private const int ChecksumOffset = HistoryDepthOffset + 2;

    private static readonly byte[] ValueA = Bytes.FromHexString("0x0a");
    private static readonly byte[] ValueB = Bytes.FromHexString("0x0b");

    public enum FormatField
    {
        Magic,
        Version,
        NextFreePage
    }

    [Test]
    public async Task Fresh_store_reopens_empty()
    {
        using TempDirectory dir = new();
        Open(dir, HistoryDepth).Dispose();

        using PagedDb db = Open(dir, HistoryDepth);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.NextFreePage, Is.EqualTo(HistoryDepth));
            Assert.That(db.ReadAccount(), Is.Empty);
        }

        await db.SetAccountAndCommit(ValueA);
        Assert.That(db.ReadAccount(), Is.EqualTo(ValueA));
    }

    [Test]
    public async Task Reopen_with_a_different_history_depth_throws([Values(3, 4)] int historyDepth)
    {
        using TempDirectory dir = new();
        using (PagedDb db = Open(dir, HistoryDepth))
        {
            await db.SetAccountAndCommit(ValueA);
        }

        Assert.That(() => Open(dir, (byte)historyDepth), Throws.InvalidOperationException.With.Message.Contains("history depth"));
    }

    [Test]
    public async Task Reopen_rejects_unknown_format([Values] FormatField field)
    {
        using TempDirectory dir = new();
        using (PagedDb db = Open(dir, HistoryDepth))
        {
            await db.SetAccountAndCommit(ValueA);
        }

        // Batch 1 is the latest root, in slot 1. Resealing it keeps the edit from reading as a torn write.
        EditRoot(dir, 1, root =>
        {
            switch (field)
            {
                case FormatField.Magic:
                    Bytes.FromHexString("0x50505251").CopyTo(root, MagicOffset);
                    break;
                case FormatField.Version:
                    BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(VersionOffset), 2);
                    break;
                case FormatField.NextFreePage:
                    BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(PageHeader.Size), (uint)(Size / Page.PageSize) + 1);
                    break;
            }

            Reseal(root);
        });

        Assert.That(() => Open(dir, HistoryDepth), Throws.InvalidOperationException);
    }

    [Test]
    public async Task Reopen_falls_back_to_previous_root_when_latest_is_torn()
    {
        using TempDirectory dir = new();
        using (PagedDb db = Open(dir, HistoryDepth))
        {
            await db.SetAccountAndCommit(ValueA);
            await db.SetAccountAndCommit(ValueB);
        }

        // Batch 2 is in slot 0.
        EditRoot(dir, 0, Tear);

        using (PagedDb db = Open(dir, HistoryDepth))
        {
            using (IReadOnlyBatch read = db.BeginReadOnlyBatch())
            {
                read.VerifyNoPagesMissing();
            }

            Assert.That(db.ReadAccount(), Is.EqualTo(ValueA));
            await db.SetAccountAndCommit(ValueB);
        }

        using PagedDb reopened = Open(dir, HistoryDepth);
        Assert.That(reopened.ReadAccount(), Is.EqualTo(ValueB));
    }

    [Test]
    public async Task A_torn_first_write_reopens_fresh()
    {
        using TempDirectory dir = new();
        using (PagedDb db = Open(dir, HistoryDepth))
        {
            await db.SetAccountAndCommit(ValueA);
        }

        // The first commit tore both slots: slot 0 with the stamp of the new file, slot 1 with batch 1.
        EditRoot(dir, 0, Tear);
        EditRoot(dir, 1, Tear);

        using PagedDb reopened = Open(dir, HistoryDepth);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reopened.NextFreePage, Is.EqualTo(HistoryDepth));
            Assert.That(reopened.ReadAccount(), Is.Empty);
        }
    }

    [Test]
    public async Task Reopen_rejects_file_without_intact_root([Values] bool latestCopiedToOtherSlot)
    {
        using TempDirectory dir = new();
        using (PagedDb db = Open(dir, HistoryDepth))
        {
            await db.SetAccountAndCommit(ValueA);
            await db.SetAccountAndCommit(ValueB);
        }

        // Batch 2 is in slot 0. A copy of it in slot 1 is intact, but its batch id belongs to slot 0.
        byte[] latest = [];
        EditRoot(dir, 0, root => latest = (byte[])root.Clone());
        EditRoot(dir, 1, latestCopiedToOtherSlot ? root => latest.CopyTo(root, 0) : Tear);
        EditRoot(dir, 0, Tear);

        Assert.That(() => Open(dir, HistoryDepth), Throws.InvalidOperationException.With.Message.Contains("corrupt"));
    }

    [Test]
    public void Size_is_validated([Values(3L * Page.PageSize + 1, 2L * Page.PageSize, OneTiB + Page.PageSize)] long size) =>
        Assert.That(() => new NativeMemoryPageManager(size, HistoryDepth), Throws.InstanceOf<ArgumentOutOfRangeException>());

    [Test]
    public void Root_payload_fits_the_page()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RootPage.Payload.AbandonedStart, Is.EqualTo(3656));
            Assert.That(AbandonedList.MaxCount, Is.EqualTo(104));
            Assert.That(RootPage.Payload.AbandonedStart + AbandonedList.Size, Is.LessThanOrEqualTo(Page.PageSize - PageHeader.Size));
            Assert.That(Unsafe.SizeOf<RootPage.Payload>(), Is.EqualTo(Page.PageSize - PageHeader.Size));
        }
    }

    private static PagedDb Open(TempDirectory dir, byte historyDepth) => PagedDb.MemoryMappedDb(Size, historyDepth, dir.Path, accessObserver: null);

    private static void EditRoot(TempDirectory dir, int slot, Action<byte[]> edit)
    {
        using SafeFileHandle file = File.OpenHandle(MemoryMappedPageManager.GetPaprikaFilePath(dir.Path), FileMode.Open, FileAccess.ReadWrite);
        byte[] root = new byte[Page.PageSize];
        long offset = (long)slot * Page.PageSize;
        RandomAccess.Read(file, root, offset);
        edit(root);
        RandomAccess.Write(file, root, offset);
    }

    /// <summary>Changes the last byte of the root, as a write that tore before its last sector would.</summary>
    private static void Tear(byte[] root) => root[^1] ^= 0xFF;

    /// <summary>Recomputes the checksum: the complement of CRC32C over the page's 64-bit words, the checksum read as zero.</summary>
    private static void Reseal(byte[] root)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(ChecksumOffset), 0);
        uint crc = uint.MaxValue;
        foreach (ulong word in MemoryMarshal.Cast<byte, ulong>(root))
        {
            crc = BitOperations.Crc32C(crc, word);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(ChecksumOffset), ~crc);
    }
}
