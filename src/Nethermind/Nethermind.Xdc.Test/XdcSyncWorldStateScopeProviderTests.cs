// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Xdc.Contracts;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
internal class XdcSyncWorldStateScopeProviderTests
{
    [Test]
    public void BeginScope_ReadsAccountAndStorageFromCompletedSyncRoot()
    {
        (XdcSyncWorldStateScopeProvider provider, IPersistence.IPersistenceReader reader, BlockHeader header, Address address, MemDb codeDb) =
            CreateProvider();

        using (provider)
        using (codeDb)
        {
            using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(header, new LocalMetrics()))
            {
                Assert.That(scope.Get(address)!.Balance, Is.EqualTo((UInt256)1));
                Assert.That(scope.CreateStorageTree(address).Get(UInt256.One), Is.EqualTo(new byte[] { 0x42 }));

                ValueHash256 codeHash = TestItem.KeccakA.ValueHash256;
                using IWorldStateScopeProvider.ICodeSetter codeSetter = scope.CodeDb.BeginCodeWrite();
                void SetCode() => codeSetter.Set(in codeHash, [0x01]);
                Assert.That(SetCode, Throws.TypeOf<InvalidOperationException>());
            }

            Assert.That(codeDb.Get(TestItem.KeccakA.Bytes), Is.Null);
            reader.Received(1).Dispose();
        }
    }

    [Test]
    public void ReadOnlyTransactionFactory_UsesCompletedSyncStateForConstantContract()
    {
        (IPersistence persistence, IPersistence.IPersistenceReader reader, BlockHeader header, Address contractAddress, MemDb codeDb) =
            CreateContractFixture();

        using (codeDb)
        using (IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true }))
            .AddKeyedSingleton<IDb>(DbNames.Code, codeDb)
            .AddSingleton<IPersistence>(persistence)
            .AddSingleton<XdcSyncReadOnlyTxProcessingEnvFactory>()
            .Build())
        {
            XdcSyncReadOnlyTxProcessingEnvFactory factory = container.Resolve<XdcSyncReadOnlyTxProcessingEnvFactory>();
            MasternodeVotingContract contract = new(container.Resolve<IAbiEncoder>(), contractAddress, factory);

            Assert.That(contract.GetCandidates(header), Is.EqualTo(new[] { TestItem.AddressB }));
        }
        reader.Received().Dispose();
    }

    [Test]
    public void BeginScope_RejectsMissingSyncRoot()
    {
        (XdcSyncWorldStateScopeProvider provider, IPersistence.IPersistenceReader reader, BlockHeader header, _, MemDb codeDb) =
            CreateProvider();
        header.StateRoot = TestItem.KeccakA;

        using (provider)
        using (codeDb)
        {
            Assert.That(() => provider.BeginScope(header, new LocalMetrics()), Throws.TypeOf<MissingTrieNodeException>());
        }
        reader.Received(1).Dispose();
    }

    [Test]
    public void BeginScope_RejectsIncorrectSyncRootContents()
    {
        (XdcSyncWorldStateScopeProvider provider, IPersistence.IPersistenceReader reader, BlockHeader header, _, MemDb codeDb) =
            CreateProvider();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => [0x80]);

        using (provider)
        using (codeDb)
        {
            Assert.That(() => provider.BeginScope(header, new LocalMetrics()), Throws.TypeOf<MissingTrieNodeException>());
        }
        reader.Received(1).Dispose();
    }

    private static (XdcSyncWorldStateScopeProvider Provider, IPersistence.IPersistenceReader Reader, BlockHeader Header, Address Address, MemDb CodeDb) CreateProvider()
    {
        Address address = TestItem.AddressA;
        MemoryNodeStorage nodeStorage = new();

        StorageTree storageTree = new(
            new RawScopedTrieStore(nodeStorage, address.ToAccountPath.ToHash256()),
            Keccak.EmptyTreeHash,
            LimboLogs.Instance);
        storageTree.Set(UInt256.One, [0x42]);
        storageTree.Commit();

        StateTree stateTree = new(new RawScopedTrieStore(nodeStorage), LimboLogs.Instance);
        stateTree.Set(address, Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject);
        stateTree.Commit();

        byte[] stateRootRlp = nodeStorage.Get(null, TreePath.Empty, stateTree.RootHash.ValueHash256)!;
        byte[] storageRootRlp = nodeStorage.Get(address.ToAccountPath.ToHash256(), TreePath.Empty, storageTree.RootHash.ValueHash256)!;

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => stateRootRlp);
        reader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => storageRootRlp);

        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader(ReaderFlags.Sync).Returns(reader);

        IWorldStateScopeProvider normalProvider = Substitute.For<IWorldStateScopeProvider>();
        normalProvider.HasRoot(Arg.Any<BlockHeader?>()).Returns(false);

        MemDb codeDb = new();
        XdcSyncWorldStateScopeProvider provider = new(normalProvider, persistence, codeDb, LimboLogs.Instance);
        BlockHeader header = new BlockHeaderBuilder().WithStateRoot(stateTree.RootHash).TestObject;
        return (provider, reader, header, address, codeDb);
    }

    private static (IPersistence Persistence, IPersistence.IPersistenceReader Reader, BlockHeader Header, Address ContractAddress, MemDb CodeDb) CreateContractFixture()
    {
        Address contractAddress = TestItem.AddressA;
        Address candidate = TestItem.AddressB;
        byte[] contractCode = CreateCandidateReturningCode(candidate);
        ValueHash256 codeHash = ValueKeccak.Compute(contractCode);

        MemoryNodeStorage nodeStorage = new();
        StateTree stateTree = new(new RawScopedTrieStore(nodeStorage), LimboLogs.Instance);
        stateTree.Set(contractAddress, Build.An.Account
            .WithBalance(1)
            .WithCode(contractCode)
            .TestObject);
        stateTree.Commit();

        byte[] stateRootRlp = nodeStorage.Get(null, TreePath.Empty, stateTree.RootHash.ValueHash256)!;
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => stateRootRlp);

        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader(ReaderFlags.Sync).Returns(reader);

        MemDb codeDb = new();
        codeDb.Set(codeHash.Bytes, contractCode);
        BlockHeader header = new BlockHeaderBuilder().WithStateRoot(stateTree.RootHash).TestObject;
        return (persistence, reader, header, contractAddress, codeDb);
    }

    private static byte[] CreateCandidateReturningCode(Address candidate)
    {
        byte[] prefix = [0x60, 0x20, 0x60, 0x00, 0x52, 0x60, 0x01, 0x60, 0x20, 0x52, 0x73];
        byte[] suffix = [0x60, 0x40, 0x52, 0x60, 0x60, 0x60, 0x00, 0xf3];
        byte[] code = new byte[prefix.Length + Address.Size + suffix.Length];
        prefix.CopyTo(code, 0);
        candidate.Bytes.CopyTo(code.AsSpan(prefix.Length));
        suffix.CopyTo(code, prefix.Length + Address.Size);
        return code;
    }
}
