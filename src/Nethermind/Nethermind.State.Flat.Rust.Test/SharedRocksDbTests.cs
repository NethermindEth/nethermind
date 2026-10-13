// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>The native store and C# on one RocksDB instance, opened by C# the way the flat DB is.</summary>
[Parallelizable(ParallelScope.All)]
public class SharedRocksDbTests
{
    private static readonly FlatLayout[] Layouts = GoldenVectorGenerator.Layouts;
    private static readonly Address Address = new("0xaaaaaaaa00000000000000000000000000000001");
    private static readonly TreePath ShortPath = TreePath.FromHexString("12");
    private static readonly TreePath LongPath = TreePath.FromHexString("123456789abcdef0123");

    [Test]
    public async Task Rust_and_csharp_read_each_others_writes([ValueSource(nameof(Layouts))] FlatLayout layout)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using IColumnsDb<FlatDbColumns> db = FlatTestDbs.OpenRocksDb(path.Path);
        StateId rustState = new(1, Keccak.Compute("1"));
        StateId csharpState = new(2, Keccak.Compute("2"));

        using RustPersistence rust = new(db, new FlatDbConfig { Layout = layout }, LimboLogs.Instance);
        Write(rust, StateId.PreGenesis, rustState, 1, Bytes.FromHexString("0xc101"));

        IPersistence csharp = FlatTestDbs.CreateCSharpStack(db, layout, carryForward: true);
        try
        {
            using (IPersistence.IPersistenceReader reader = csharp.CreateReader())
            {
                AssertReads(reader, rustState, 1, Bytes.FromHexString("0xc101"));
            }

            Write(csharp, rustState, csharpState, 2, Bytes.FromHexString("0xc102"));
        }
        finally
        {
            await ((IAsyncDisposable)csharp).DisposeAsync();
        }

        // Its caches know nothing of the C# write, so the read bypasses them.
        using IPersistence.IPersistenceReader rustReader = rust.CreateReader(ReaderFlags.Sync);
        AssertReads(rustReader, csharpState, 2, Bytes.FromHexString("0xc102"));
    }

    [Test]
    public void Disposing_the_db_waits_for_the_store_or_leaves_it_the_close([Values] bool storeOutlivesDrainTimeout)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        FlatTestDbs.TrackingColumnsDb db = new(path.Path, storeOutlivesDrainTimeout ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(10), LimboLogs.Instance);
        RustPersistence persistence = new(db, new FlatDbConfig(), LimboLogs.Instance);
        Write(persistence, StateId.PreGenesis, new StateId(1, Keccak.Compute("1")), 1, Bytes.FromHexString("0xc0"));

        Task disposing = Task.Run(db.Dispose);
        if (storeOutlivesDrainTimeout)
        {
            Assert.That(disposing.Wait(TimeSpan.FromSeconds(5)), "dispose waited past its drain timeout");
            Assert.That(db.NativeCloses, Is.Zero, "the database closed under the store");
        }
        else
        {
            Assert.That(disposing.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "the database closed under the store");
        }

        persistence.Dispose();

        // Finishing well within the 10 s drain timeout proves the store released its lease.
        Assert.That(disposing.Wait(TimeSpan.FromSeconds(5)), "dispose never finished after the store was released");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.NativeCloses, Is.EqualTo(1), "the database must close exactly once");
            Assert.That(db.AcquireNativeLease, Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    private static void Write(IPersistence persistence, StateId from, StateId to, ulong value, byte[] rlp)
    {
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(from, to);
        batch.SetAccount(Address, new Account(value, value));
        batch.SetStorage(Address, 1, value);
        batch.SetStateTrieNode(ShortPath, rlp);
        batch.SetStateTrieNode(LongPath, rlp);
        batch.SetStorageTrieNode(Keccak.Compute(Address.Bytes), LongPath, rlp);
    }

    private static void AssertReads(IPersistence.IPersistenceReader reader, StateId state, ulong value, byte[] rlp)
    {
        UInt256 slot = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(state));
            Assert.That(reader.GetAccount(Address)?.Balance, Is.EqualTo((UInt256)value));
            Assert.That(reader.TryGetSlot(Address, 1, ref slot), Is.True);
            Assert.That(slot, Is.EqualTo((UInt256)value));
            Assert.That(reader.TryLoadStateRlp(ShortPath, ReadFlags.None), Is.EqualTo(rlp));
            Assert.That(reader.TryLoadStateRlp(LongPath, ReadFlags.None), Is.EqualTo(rlp));
            Assert.That(reader.TryLoadStorageRlp(Keccak.Compute(Address.Bytes), LongPath, ReadFlags.None), Is.EqualTo(rlp));
        }
    }
}
