// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat.History;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class SeedFlatHistoryGenesisTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryWriter _writer = null!;
    private HistoryReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        FlatDbConfig config = new() { HistoryEnabled = true };
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        HistoryRowFormat rowFormat = HistoryRowFormat.Resolve(availability, config);
        _writer = new HistoryWriter(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance, commitments: null);
        _reader = new HistoryReader(_db, _historyColumns, availability, rowFormat, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public async Task Seeds_chain_spec_allocations_readable_at_genesis_and_later()
    {
        byte[] code = [0x60, 0x00];
        ChainSpec chainSpec = new()
        {
            Allocations = new()
            {
                [TestItem.AddressA] = new ChainSpecAllocation(1000),
                [TestItem.AddressB] = new ChainSpecAllocation(5, 1, code, constructor: null, storage: null),
            },
        };

        await Step(chainSpec).Execute(CancellationToken.None);

        _reader.TryGetAccount(0, TestItem.AddressA, out AccountStruct balanceOnly);
        _reader.TryGetAccount(9, TestItem.AddressB, out AccountStruct withCode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.HasHistoryForBlock(0), Is.True);
            Assert.That(balanceOnly.Balance, Is.EqualTo((UInt256)1000));
            Assert.That(withCode.Balance, Is.EqualTo((UInt256)5));
            Assert.That(withCode.Nonce, Is.EqualTo((ulong)1));
            Assert.That(withCode.CodeHash, Is.EqualTo(ValueKeccak.Compute(code)));
        }
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task Skips_seeding_for_unreconstructible_allocations(bool hasConstructor, bool hasStorage)
    {
        ChainSpec chainSpec = new()
        {
            Allocations = new()
            {
                [TestItem.AddressA] = new ChainSpecAllocation(
                    1000, 0,
                    code: null,
                    constructor: hasConstructor ? [0x60, 0x00] : null,
                    storage: hasStorage ? new() { [UInt256.One] = [0x0a] } : null),
            },
        };

        await Step(chainSpec).Execute(CancellationToken.None);

        Assert.That(_reader.HasHistoryForBlock(0), Is.False);
    }

    // A genuinely fresh DB has no genesis header yet (the step runs before InitializeBlockchain executes genesis);
    // that node captures block 0 through the normal walk, so the step must not seed.
    [Test]
    public async Task Skips_seeding_on_fresh_db_without_genesis_header()
    {
        ChainSpec chainSpec = new() { Allocations = new() { [TestItem.AddressA] = new ChainSpecAllocation(1000) } };

        await Step(chainSpec, hasGenesis: false).Execute(CancellationToken.None);

        Assert.That(_reader.HasHistoryForBlock(0), Is.False);
    }

    // An existing DB stopped right after genesis (head 0, genesis header present) never re-executes genesis, so the
    // walk alone can never connect to block 0 — the step must seed it.
    [Test]
    public async Task Seeds_existing_db_with_head_at_genesis()
    {
        ChainSpec chainSpec = new() { Allocations = new() { [TestItem.AddressA] = new ChainSpecAllocation(1000) } };

        await Step(chainSpec, headBlockNumber: 0).Execute(CancellationToken.None);

        Assert.That(_reader.HasHistoryForBlock(0), Is.True);
    }

    private SeedFlatHistoryGenesis Step(ChainSpec chainSpec, int headBlockNumber = 100, bool hasGenesis = true) =>
        new(CreatePolicy(), chainSpec, BlockTree(headBlockNumber, hasGenesis), _writer, _reader, LimboLogs.Instance);

    private static IBlockTree BlockTree(int headBlockNumber = 100, bool hasGenesis = true)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(headBlockNumber).TestObject);
        blockTree.Genesis.Returns(hasGenesis ? Build.A.BlockHeader.WithNumber(0).WithStateRoot(TestItem.KeccakA).TestObject : null);
        return blockTree;
    }

    private static FlatStateActivationPolicy CreatePolicy()
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(true);
        flatDbConfig.ImportFromPruningTrieState.Returns(false);
        flatDbConfig.Layout.Returns(FlatLayout.Flat);

        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.StateDbKeyScheme.Returns("Current");
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(new StateId(1, Keccak.Zero));
        IPersistence flatPersistence = Substitute.For<IPersistence>();
        flatPersistence.CreateReader().Returns(reader);

        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        IFileSystem fileSystem = Substitute.For<IFileSystem>();

        return new FlatStateActivationPolicy(
            flatDbConfig,
            initConfig,
            new TestHardwareInfo(32L * 1024 * 1024 * 1024),
            new Lazy<IPersistence>(() => flatPersistence),
            dbFactory,
            fileSystem,
            LimboLogs.Instance);
    }
}
