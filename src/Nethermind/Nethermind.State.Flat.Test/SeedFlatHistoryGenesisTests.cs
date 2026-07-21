// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat.History;
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
        _writer = new HistoryWriter(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, LimboLogs.Instance);
        _reader = new HistoryReader(_db, _historyColumns, LimboLogs.Instance);
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

    // A fresh from-genesis node (head still at genesis) captures block 0 through the normal walk, so the step must
    // not seed or warn even when the allocations are unreconstructible — the walk covers them properly.
    [Test]
    public async Task Skips_seeding_on_fresh_from_genesis_node()
    {
        ChainSpec chainSpec = new()
        {
            Allocations = new()
            {
                [TestItem.AddressA] = new ChainSpecAllocation(
                    1000, 0, code: null, constructor: [0x60, 0x00], storage: null),
            },
        };

        await Step(chainSpec, headBlockNumber: 0).Execute(CancellationToken.None);

        Assert.That(_reader.HasHistoryForBlock(0), Is.False);
    }

    private SeedFlatHistoryGenesis Step(ChainSpec chainSpec, int headBlockNumber = 100)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(headBlockNumber).TestObject);
        blockTree.Genesis.Returns(Build.A.BlockHeader.WithNumber(0).WithStateRoot(TestItem.KeccakA).TestObject);
        return new(chainSpec, blockTree, _writer, _reader, LimboLogs.Instance);
    }
}
