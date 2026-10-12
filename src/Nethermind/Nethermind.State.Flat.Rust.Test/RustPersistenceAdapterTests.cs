// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>The paths of <see cref="RustPersistence"/> that exist only on the C# side of the native calls.</summary>
[Parallelizable(ParallelScope.All)]
public class RustPersistenceAdapterTests
{
    private static readonly StateId Block1 = new(1, Keccak.Compute("1"));

    /// <remarks>Lengths around the stack buffer reads go through first, and one beyond the buffer of the batch's op stream.</remarks>
    [Test]
    public void Nodes_of_any_length_are_written_and_read_whole([Values(1024, 1025, 70_000)] int length, [Values] FlatTestDbs.Kind dbKind)
    {
        RustNativeLibrary.Require();
        TreePath path = TreePath.FromHexString("123");
        Hash256 owner = Keccak.Compute("owner");
        byte[] node = [.. Enumerable.Range(0, length).Select(static i => (byte)i)];
        using TempPath tempPath = TempPath.GetTempDirectory();
        using IColumnsDb<FlatDbColumns> db = FlatTestDbs.Open(dbKind, tempPath.Path);
        using RustPersistence persistence = new(db, new FlatDbConfig(), LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, Block1))
        {
            batch.SetStateTrieNode(path, node);
            batch.SetStorageTrieNode(owner, path, node);
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.TryLoadStateRlp(path, ReadFlags.None), Is.EqualTo(node));
            Assert.That(reader.TryLoadStorageRlp(owner, path, ReadFlags.None), Is.EqualTo(node));
        }
    }

    [Test]
    public void Exceptions_of_a_managed_database_reach_the_caller_as_thrown()
    {
        RustNativeLibrary.Require();
        IOException failure = new("injected flush failure");
        using FlushFailingColumnsDb db = new() { Failure = failure };
        using RustPersistence persistence = new(db, new FlatDbConfig(), LimboLogs.Instance);

        Assert.That(persistence.Flush, Throws.Exception.SameAs(failure));
        Assert.That(() => persistence.CreateWriteBatch(StateId.PreGenesis, Block1).Dispose(), Throws.Exception.SameAs(failure), "the commit syncs the write-ahead log");
        db.Failure = null;
        Assert.That(persistence.Flush, Throws.Nothing, "the exception is not thrown again");
    }

    /// <summary>In-memory columns whose flush throws <see cref="Failure"/>.</summary>
    private sealed class FlushFailingColumnsDb : IColumnsDb<FlatDbColumns>
    {
        private readonly SnapshotableMemColumnsDb<FlatDbColumns> _inner = new();

        public Exception? Failure { get; set; }

        public IEnumerable<FlatDbColumns> ColumnKeys => _inner.ColumnKeys;

        public IDb GetColumnDb(FlatDbColumns key) => _inner.GetColumnDb(key);

        public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch() => _inner.StartWriteBatch();

        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() => _inner.CreateSnapshot();

        public void Flush(bool onlyWal = false)
        {
            if (Failure is not null) throw Failure;
        }

        public void Dispose() => _inner.Dispose();
    }
}
