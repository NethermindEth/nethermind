// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSelfImportTests
{
    private const ulong AnchorBlock = 5;
    private const ulong SourceBlock = 7;
    private static readonly Hash256 SourceStateRoot = TestItem.KeccakA;
    private static readonly byte[] ContractCode = Bytes.FromHexString("0x6001600055");

    /// <remarks>
    /// The preimages list the anchor's state, which still holds an account and a slot the flat state no longer has.
    /// A slot of an anchor account and a whole account created since are listed only by the BALs after the anchor:
    /// the first as a write, the second's slot as a read.
    /// </remarks>
    [Test]
    public async Task Imports_flat_state_through_preimages_and_bals(
        [Values("complete", "unlisted-account", "unlisted-slot", "anchor-before-bal")] string scenario)
    {
        Dictionary<string, byte[]> model = [];
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        RocksDbPersistence flatSource = new(flatDb, LimboLogs.Instance, Flat.Persistence.TrieNodeLog.NullTrieNodeLog.Instance);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(StateId.PreGenesis, new StateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            SetAccount(batch, model, TestItem.AddressA, new Account(1, 100), null);
            SetAccount(batch, model, TestItem.AddressB, new Account(3, 42), ContractCode);
            SetSlot(batch, model, TestItem.AddressB, 5, 0xAB);
            SetSlot(batch, model, TestItem.AddressB, 1000, 0x1234);
            SetAccount(batch, model, TestItem.AddressC, new Account(2, 7), null);
            SetSlot(batch, model, TestItem.AddressC, 2000, 0x55);
        }
        using MemDb codeDb = new();
        codeDb[Keccak.Compute(ContractCode).Bytes] = ContractCode;

        using MemoryStream preimages = new();
        PbtPreimageCodec.Write(preimages, Preimages(new()
        {
            [TestItem.AddressA] = [],
            [TestItem.AddressB] = [5, 77],
            [TestItem.AddressD] = [9],
        }), CancellationToken.None);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);
        BlockAccessListStore balStore = new(new MemDb());
        BlockHeader anchor = AddBlock(blockTree, balStore, AnchorBlock, Keccak.Zero, bal: null);
        AddBlock(blockTree, balStore, AnchorBlock + 1, Keccak.Zero, scenario == "anchor-before-bal" ? null : Bal(
            new ReadOnlyAccountChanges(TestItem.AddressB, scenario == "unlisted-slot" ? [] : [new ReadOnlySlotChanges(1000, [new StorageChange(1, 0x1234)])], [], [], [], [])));
        AddBlock(blockTree, balStore, SourceBlock, SourceStateRoot, Bal(scenario == "unlisted-account" ? [] :
            [new ReadOnlyAccountChanges(TestItem.AddressC, [], [2000], [new BalanceChange(1, 7)], [new NonceChange(1, 2)], [])]));

        using TempPath scratch = TempPath.GetTempDirectory();
        PbtConfig config = new() { ImportSortBufferBytes = 1024 };
        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        await using PbtTestContext pbt = new(pbtDb, config);
        PbtRocksDbPersistence target = new(pbtDb, config, NullTrieNodeLog.Instance);
        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns(scratch.Path);
        PbtSelfImport selfImport = new(flatSource, codeDb, pbtDb, target, pbt.PersistenceManager,
            new PbtLeafIngestion(target, new PbtRebuilder(target, config, config.ImportConcurrency, LimboLogs.Instance), dbFactory, config, LimboLogs.Instance),
            blockTree, new BalFetcher(Substitute.For<ISyncPeerPool>(), blockTree, balStore, new SyncConfig(), LimboLogs.Instance),
            balStore, dbFactory, config, LimboLogs.Instance);

        if (scenario != "complete")
        {
            InvalidDataException exception = (await Assert.ThrowsAsync<InvalidDataException>(() => selfImport.Import(preimages, anchor, CancellationToken.None)))!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.Message, Does.Contain(scenario == "anchor-before-bal" ? "EIP-7928" : "has no preimage"));
                Assert.That(target.IsValid, Is.False);
            }
            return;
        }

        await selfImport.Import(preimages, anchor, CancellationToken.None);

        using IPbtPersistence.IReader reader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
            Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressC)!.Nonce, Is.EqualTo(2UL));
            Assert.That(PbtTestLeaves.ReadSlot(reader, TestItem.AddressB, 1000), Is.EqualTo((UInt256)0x1234));
            Assert.That(PbtTestLeaves.ReadSlot(reader, TestItem.AddressC, 2000), Is.EqualTo((UInt256)0x55));
            Assert.That(reader.GetCode(ValueKeccak.Compute(ContractCode))!.Code.ToArray(), Is.EqualTo(ContractCode));
            Assert.That(Directory.GetFileSystemEntries(Path.Combine(scratch.Path, "import")), Is.Empty);
        }
    }

    /// <summary>The preimages of <paramref name="slots"/>, ordered by Keccak path as the codec requires.</summary>
    private static IEnumerable<PbtAccountPreimages> Preimages(Dictionary<Address, UInt256[]> slots) =>
        slots.OrderBy(static account => ValueKeccak.Compute(account.Key.Bytes).ToString())
            .Select(static account =>
            {
                ValueHash256[] keys = [.. account.Value.Select(static slot => slot.ToValueHash()).OrderBy(static slot => ValueKeccak.Compute(slot.Bytes).ToString())];
                return new PbtAccountPreimages(account.Key, (uint)keys.Length, keys);
            });

    private static byte[] Bal(params ReadOnlyAccountChanges[] accounts) =>
        BlockAccessListDecoder.EncodeToBytes(new ReadOnlyBlockAccessList([.. accounts.OrderBy(static account => account.Address)], accounts.Length));

    private static BlockHeader AddBlock(IBlockTree blockTree, BlockAccessListStore balStore, ulong number, Hash256 stateRoot, byte[]? bal)
    {
        BlockHeaderBuilder builder = Build.A.BlockHeader.WithNumber(number).WithStateRoot(stateRoot);
        if (bal is not null) builder = builder.WithBlockAccessListHash(Keccak.Compute(bal));
        BlockHeader header = builder.TestObject;
        if (bal is not null) balStore.Insert(number, header.Hash!, bal);
        blockTree.FindHeader(number).Returns(header);
        blockTree.FindHeader(number, Arg.Any<BlockTreeLookupOptions>()).Returns(header);
        return header;
    }

    private static void SetAccount(IPersistence.IWriteBatch batch, Dictionary<string, byte[]> model, Address address, Account account, byte[]? code)
    {
        PbtReferenceModel.SetAccount(model, address, (ulong)account.Nonce, account.Balance, code);
        batch.SetAccount(address, code is null ? account : account.WithChangedCodeHash(Keccak.Compute(code)));
    }

    private static void SetSlot(IPersistence.IWriteBatch batch, Dictionary<string, byte[]> model, Address address, in UInt256 slot, in UInt256 value)
    {
        PbtReferenceModel.SetSlot(model, address, slot, value);
        batch.SetStorage(address, slot, value);
    }
}
