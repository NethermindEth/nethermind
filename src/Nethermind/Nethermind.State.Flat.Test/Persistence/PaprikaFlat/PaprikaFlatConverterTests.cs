// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using NUnit.Framework;
using Paprika.Store.PageManagers;
using Testably.Abstractions;

namespace Nethermind.State.Flat.Test.Persistence.PaprikaFlat;

public class PaprikaFlatConverterTests
{
    private const long FileSize = 64 * 1024 * 1024;
    private static readonly IFileSystem FileSystem = new RealFileSystem();
    private static readonly StateId Converted = new(1, ValueKeccak.Compute("converted"));
    private static readonly StateId Next = new(2, ValueKeccak.Compute("next"));
    private static readonly Account Plain = new(1, 2);
    private static readonly Account Contract = new(3, 4, Keccak.Compute("storage root"), Keccak.Compute("code"));
    private static readonly Account Written = new(5, 6);
    private static readonly TreePath NodePath = TreePath.FromHexString("12345");
    private static readonly byte[] Node = Bytes.FromHexString("0xc28080");

    private TempPath _directory = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;

    private string LogDirectory => Path.Combine(_directory.Path, "flatTrieNodeLog");
    private string PaprikaDirectory => Path.Combine(_directory.Path, "flatPaprika");
    private string PaprikaFile => MemoryMappedPageManager.GetPaprikaFilePath(PaprikaDirectory);

    public enum Refusal
    {
        OtherLayout,
        PaprikaFileExists,
        NoState,
    }

    [SetUp]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _directory.Dispose();
    }

    [Test]
    public async Task A_converted_flat_db_opens_as_paprika_flat([Values] bool trieNodeLog)
    {
        FlatDbConfig flatConfig = new() { Layout = FlatLayout.Flat, TrieNodeLogEnabled = trieNodeLog, PaprikaFlatFileSizeBytes = FileSize, PaprikaFlatPageCacheBytes = 0 };
        await WriteFlatState(flatConfig, (persistence, log) =>
            new PaprikaFlatConverter(persistence, log, _db, flatConfig, LimboLogs.Instance).Convert(PaprikaDirectory, CancellationToken.None));

        FlatDbConfig paprikaConfig = new() { Layout = FlatLayout.PaprikaFlat, TrieNodeLogEnabled = true };
        using PaprikaFlatStore store = new(_db, PaprikaDirectory, () => PaprikaFlatStore.OpenFile(PaprikaDirectory, FileSize, pageCacheBytes: 0, LimboLogs.Instance), TimeSpan.FromSeconds(1), LimboLogs.Instance);
        await using TrieNodeLog log = new(LogDirectory, FileSystem, _db, store, paprikaConfig, LimboLogs.Instance);
        PaprikaFlatPersistence persistence = new(_db, store, log, paprikaConfig);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(Converted, Next, WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressC, Written);
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        UInt256 first = default;
        UInt256 second = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(Next));
            Assert.That(reader.GetAccount(TestItem.AddressA), Is.EqualTo(Plain));
            Assert.That(reader.GetAccount(TestItem.AddressB), Is.EqualTo(Contract));
            Assert.That(reader.GetAccount(TestItem.AddressC), Is.EqualTo(Written));
            Assert.That(reader.TryGetSlot(TestItem.AddressB, 1, ref first) ? first : (UInt256?)null, Is.EqualTo((UInt256)5));
            Assert.That(reader.TryGetSlot(TestItem.AddressB, 2, ref second) ? second : (UInt256?)null, Is.EqualTo(UInt256.MaxValue));
            Assert.That(reader.TryLoadStateRlp(NodePath, ReadFlags.None), Is.EqualTo(Node));
            Assert.That(((ISortedKeyValueStore)_db.GetColumnDb(FlatDbColumns.Account)).FirstKey, Is.Null);
            Assert.That(((ISortedKeyValueStore)_db.GetColumnDb(FlatDbColumns.Storage)).FirstKey, Is.Null);
        }
    }

    [Test]
    public async Task Refuses_and_keeps_the_flat_db([Values] Refusal refusal)
    {
        FlatDbConfig config = new() { Layout = refusal == Refusal.OtherLayout ? FlatLayout.FlatInTrie : FlatLayout.Flat, PaprikaFlatFileSizeBytes = FileSize, PaprikaFlatPageCacheBytes = 0 };
        RocksDbPersistence persistence = new(_db, LimboLogs.Instance, NullTrieNodeLog.Instance);
        if (refusal != Refusal.NoState) await WriteFlatState(new FlatDbConfig(), (_, _) => { });
        if (refusal == Refusal.PaprikaFileExists)
        {
            Directory.CreateDirectory(PaprikaDirectory);
            await File.WriteAllBytesAsync(PaprikaFile, []);
        }

        Assert.That(() => new PaprikaFlatConverter(persistence, NullTrieNodeLog.Instance, _db, config, LimboLogs.Instance).Convert(PaprikaDirectory, CancellationToken.None),
            Throws.TypeOf<InvalidConfigurationException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(PaprikaFile), Is.EqualTo(refusal == Refusal.PaprikaFileExists));
            Assert.That(BasePersistence.ReadCurrentState(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.EqualTo(refusal == Refusal.NoState ? StateId.PreGenesis : Converted));
        }
    }

    /// <summary>Persists a plain account, a contract with two slots and a state trie node as <see cref="FlatLayout.Flat"/>, then runs <paramref name="convert"/> with that persistence and its trie node log.</summary>
    private async Task WriteFlatState(FlatDbConfig config, Action<IPersistence, ITrieNodeLog> convert)
    {
        await using TrieNodeLog? log = config.TrieNodeLogEnabled ? new TrieNodeLog(LogDirectory, FileSystem, _db, new ColumnsFlatMetadataStore(_db, config), config, LimboLogs.Instance) : null;
        RocksDbPersistence persistence = new(_db, LimboLogs.Instance, (ITrieNodeLog?)log ?? NullTrieNodeLog.Instance);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, Converted, WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, Plain);
            batch.SetAccount(TestItem.AddressB, Contract);
            batch.SetStorage(TestItem.AddressB, 1, 5);
            batch.SetStorage(TestItem.AddressB, 2, UInt256.MaxValue);
            batch.SetStateTrieNode(NodePath, Node);
        }

        convert(persistence, (ITrieNodeLog?)log ?? NullTrieNodeLog.Instance);
    }
}
