// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class RootPageTests
{
    private const long Mb = 1024 * 1024;

    [Test]
    public void Destroy_is_not_supported()
    {
        using PagedDb db = PagedDb.NativeMemoryDb(Mb);
        using IBatch batch = db.BeginNextBatch();

        Assert.That(() => batch.Destroy(NibblePath.FromKey(Values.Key2)), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void State_root_map_overflow_throws([Values(0, 1)] int keyNibbles)
    {
        using PagedDb db = PagedDb.NativeMemoryDb(Mb);
        using IBatch batch = db.BeginNextBatch();
        byte[] keyBytes = Bytes.FromHexString("0xab");

        // Keys shorter than two nibbles live in the state root page's own map, which holds about 3 KB.
        Assert.That(() => batch.SetRaw(Key.Account(NibblePath.FromKey(keyBytes).SliceTo(keyNibbles)), new byte[3_500]),
            Throws.InvalidOperationException);
    }

    [TestCase(0, 40)]
    [TestCase(0, 62)]
    [TestCase(1, 64)]
    public void Storage_key_needs_a_full_keccak_account_path(int nibbleFrom, int nibbles)
    {
        using PagedDb db = PagedDb.NativeMemoryDb(Mb);
        using IBatch batch = db.BeginNextBatch();

        // 33 bytes, so that 64 nibbles also fit from an odd nibble.
        byte[] account = [.. Values.Key2.BytesAsSpan, 0x2b];
        byte[] value = Bytes.FromHexString("0x2a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account, nibbleFrom, nibbles), Values.Key0), value), Throws.ArgumentException);
            Assert.That(() => batch.TryGet(Key.StorageCell(NibblePath.FromKey(account, nibbleFrom, nibbles), Values.Key0), out _), Throws.ArgumentException);
            Assert.That(() => batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(account, nibbleFrom, nibbles), NibblePath.Empty)), Throws.ArgumentException);
        }
    }
}
