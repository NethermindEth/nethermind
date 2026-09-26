// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

[Parallelizable(ParallelScope.All)]
public class BlockAccessListRecoveryStepTests
{
    private const ulong BlockNumber = 7;

    private static readonly ReadOnlyBlockAccessList StoredBal = Build.A.BlockAccessList
        .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 5)).TestObject)
        .TestObject;

    private static readonly byte[] StoredRlp = Rlp.Encode(StoredBal).Bytes;
    private static readonly Hash256 StoredHash = Keccak.Compute(StoredRlp);

    public enum Stored { Matching, Mismatching, Undecodable, Missing }

    [TestCase(Stored.Matching, true)]
    [TestCase(Stored.Mismatching, false)]
    [TestCase(Stored.Undecodable, false)]
    [TestCase(Stored.Missing, false)]
    public void Queued_block_gets_the_stored_list_only_when_it_matches_the_header(Stored stored, bool expectAttached)
    {
        TestMemDb db = new();
        Block block = BlockCommittingTo(StoredHash);
        byte[] payload = stored switch
        {
            Stored.Matching => StoredRlp,
            Stored.Mismatching => Rlp.Encode(new ReadOnlyBlockAccessList()).Bytes,
            Stored.Undecodable => [0xc5, 0x01],
            _ => null
        };
        if (payload is not null) new BlockAccessListStore(db).Insert(BlockNumber, block.Hash!, payload);

        CreateStep(db).RecoverDataForQueuedProcessing(block);

        Assert.That(block.BlockAccessList?.WireHash, expectAttached ? Is.EqualTo(StoredHash) : Is.Null);
    }

    [Test]
    public void Non_queued_processing_is_left_on_its_own_path()
    {
        TestMemDb db = new();
        Block block = BlockCommittingTo(StoredHash);
        new BlockAccessListStore(db).Insert(BlockNumber, block.Hash!, StoredRlp);

        CreateStep(db).RecoverData(block);

        Assert.That(block.BlockAccessList, Is.Null);
    }

    [Test]
    public void Block_that_already_carries_a_list_keeps_it()
    {
        TestMemDb db = new();
        ReadOnlyBlockAccessList carried = new();
        Block block = Build.A.Block.WithNumber(BlockNumber).WithBlockAccessListHash(StoredHash).WithBlockAccessList(carried).TestObject;
        new BlockAccessListStore(db).Insert(BlockNumber, block.Hash!, StoredRlp);

        CreateStep(db).RecoverDataForQueuedProcessing(block);

        Assert.That(block.BlockAccessList, Is.SameAs(carried));
    }

    [Test]
    public void Is_a_production_preprocessor_step()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Build();

        Assert.That(container.Resolve<IReadOnlyList<IBlockPreprocessorStep>>(), Has.Some.InstanceOf<BlockAccessListRecoveryStep>());
    }

    private static Block BlockCommittingTo(Hash256 balHash) =>
        Build.A.Block.WithNumber(BlockNumber).WithBlockAccessListHash(balHash).TestObject;

    private static BlockAccessListRecoveryStep CreateStep(TestMemDb db) => new(new BlockAccessListStore(db), LimboLogs.Instance);
}
