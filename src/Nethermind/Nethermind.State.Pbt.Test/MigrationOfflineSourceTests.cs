// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Migration;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
[Platform("Linux")]
public class MigrationOfflineSourceTests
{
    [Test]
    public void Offline_reader_preserves_source_files_and_excludes_second_lease([Values(FlatLayout.PreimageFlat, FlatLayout.PreimageFlatV1)] FlatLayout layout)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        CreateSource(directory.Path, layout);
        Dictionary<string, string> before = HashFiles(directory.Path);
        using (MigrationOfflineSourceLease lease = MigrationOfflineSourceLease.Open(directory.Path, LimboLogs.Instance))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lease.Reader.IsPreimageMode, Is.True);
                Assert.That(lease.Reader.CurrentState.BlockNumber, Is.EqualTo(3));
                Assert.That(lease.Code.Get(Bytes.FromHexString("01")), Is.EqualTo(Bytes.FromHexString("6000")));
                Assert.Throws<IOException>(() => MigrationOfflineSourceLease.Open(directory.Path, LimboLogs.Instance));
            }
        }
        Assert.That(HashFiles(directory.Path), Is.EquivalentTo(before));
        using MigrationOfflineSourceLease reopened = MigrationOfflineSourceLease.Open(directory.Path, LimboLogs.Instance);
        Assert.That(reopened.Reader.CurrentState.BlockNumber, Is.EqualTo(3));
    }

    [Test]
    public void Rocks_writer_and_offline_reader_exclude_each_other([Values("flat", "code")] string databaseName)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        CreateSource(directory.Path, FlatLayout.PreimageFlat);
        string path = Path.Combine(directory.Path, databaseName);
        using DbOptions options = new();
        using ColumnFamilyOptions columnOptions = new();
        ColumnFamilies columns = new(columnOptions);
        foreach (string name in RocksDb.ListColumnFamilies(options, path)) columns.Add(name, columnOptions);
        using (RocksDb writer = RocksDb.Open(options, path, columns))
            Assert.Throws<IOException>(() => MigrationOfflineSourceLease.Open(directory.Path, LimboLogs.Instance));
        using (MigrationOfflineSourceLease lease = MigrationOfflineSourceLease.Open(directory.Path, LimboLogs.Instance))
            Assert.Throws<RocksDbNativeException>(() => { using RocksDb writer = RocksDb.Open(options, path, columns); });
        using RocksDb afterRelease = RocksDb.Open(options, path, columns);
    }

    [Test]
    public void Missing_source_or_nonempty_wal_is_rejected_without_writes([Values] bool nonemptyWal)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        if (nonemptyWal)
        {
            CreateSource(directory.Path, FlatLayout.PreimageFlat);
            File.WriteAllBytes(Path.Combine(directory.Path, "flat", "999999.log"), Bytes.FromHexString("01"));
        }
        Dictionary<string, string> before = HashFiles(directory.Path);
        Assert.That(() => MigrationOfflineSourceLease.Open(directory.Path, LimboLogs.Instance), nonemptyWal ? Throws.TypeOf<InvalidDataException>() : Throws.InstanceOf<IOException>());
        Assert.That(HashFiles(directory.Path), Is.EquivalentTo(before));
    }

    private static void CreateSource(string root, FlatLayout layout)
    {
        Directory.CreateDirectory(root);
        using DbOptions options = new();
        options.SetCreateIfMissing(true);
        options.SetCreateMissingColumnFamilies(true);
        using ColumnFamilyOptions columnOptions = new();
        ColumnFamilies columns = new(columnOptions);
        foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>()) columns.Add(column.ToString(), columnOptions);
        using (RocksDb flat = RocksDb.Open(options, Path.Combine(root, "flat"), columns))
        {
            IColumnFamilyHandle metadata = flat.GetColumnFamily(nameof(FlatDbColumns.Metadata));
            flat.Put(Keccak.Compute("Layout").Bytes, new byte[] { (byte)layout }, metadata);
            flat.Put(Keccak.Compute("SlotEncoding").Bytes, Bytes.FromHexString("01"), metadata);
            byte[] state = new byte[40];
            BinaryPrimitives.WriteUInt64BigEndian(state, 3);
            Keccak.EmptyTreeHash.Bytes.CopyTo(state.AsSpan(8));
            flat.Put(Keccak.Compute("CurrentState").Bytes, state, metadata);
            using FlushOptions flush = new();
            flush.SetWaitForFlush(true);
            flat.Flush(flush, metadata);
        }
        using (RocksDb code = RocksDb.Open(options, Path.Combine(root, "code")))
        {
            code.Put(Bytes.FromHexString("01"), Bytes.FromHexString("6000"));
            using FlushOptions flush = new();
            flush.SetWaitForFlush(true);
            code.Flush(flush);
        }
    }

    private static Dictionary<string, string> HashFiles(string root)
    {
        Dictionary<string, string> hashes = [];
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            hashes.Add(Path.GetRelativePath(root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        return hashes;
    }
}
