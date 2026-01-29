// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatOverridableWorldScopeTests
{
    private class TestContext : IDisposable
    {
        private readonly ContainerBuilder _containerBuilder;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly IPersistence.IPersistenceReader _persistenceReader;

        private IContainer? _container;
        private IContainer Container => _container ??= _containerBuilder.Build();

        public ResourcePool ResourcePool => field ??= Container.Resolve<ResourcePool>();
        public IFlatDbManager FlatDbManager => field ??= Container.Resolve<IFlatDbManager>();
        public FlatOverridableWorldScope OverridableScope => field ??= Container.Resolve<FlatOverridableWorldScope>();
        public List<(Snapshot Snapshot, TransientResource Resource)> FlatDbManagerAddSnapshotCalls { get; } = [];

        public TestContext(FlatDbConfig? config = null)
        {
            config ??= new FlatDbConfig();
            _persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();

            _containerBuilder = new ContainerBuilder()
                .AddModule(new FlatWorldStateModule(config))
                .AddSingleton<IPersistence.IPersistenceReader>(_ => _persistenceReader)
                .AddSingleton<IFlatDbManager>(ctx =>
                {
                    IFlatDbManager flatDbManager = Substitute.For<IFlatDbManager>();
                    flatDbManager.When(it => it.AddSnapshot(Arg.Any<Snapshot>(), Arg.Any<TransientResource>()))
                        .Do(c =>
                        {
                            Snapshot snapshot = (Snapshot)c[0];
                            TransientResource transientResource = (TransientResource)c[1];
                            FlatDbManagerAddSnapshotCalls.Add((snapshot, transientResource));
                        });

                    flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>())
                        .Returns(_ =>
                        {
                            SnapshotPooledList snapshotList = new(0);
                            return new ReadOnlySnapshotBundle(snapshotList, Substitute.For<IPersistence.IPersistenceReader>(), false);
                        });

                    flatDbManager.HasStateForBlock(Arg.Any<StateId>())
                        .Returns(false);

                    return flatDbManager;
                })
                .Bind<IFlatCommitTarget, IFlatDbManager>()
                .AddSingleton<IProcessExitSource>(_ => new CancellationTokenSourceProcessExitSource(_cancellationTokenSource))
                .AddSingleton<ILogManager>(LimboLogs.Instance)
                .AddSingleton<IFlatDbConfig>(config)
                .AddSingleton<ITrieNodeCache>(_ => Substitute.For<ITrieNodeCache>())
                .AddSingleton<IWorldStateScopeProvider.ICodeDb>(_ => new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new TestMemDb()));

            // Register keyed IDb for code database
            _containerBuilder.RegisterInstance<IDb>(new TestMemDb()).Keyed<IDb>(DbNames.Code);
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();

            foreach ((Snapshot snapshot, TransientResource resource) in FlatDbManagerAddSnapshotCalls)
            {
                snapshot.Dispose();
                ResourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, resource);
            }

            _container?.Dispose();
            _cancellationTokenSource.Dispose();
        }

        private class CancellationTokenSourceProcessExitSource(CancellationTokenSource cancellationTokenSource) : IProcessExitSource
        {
            public CancellationToken Token => cancellationTokenSource.Token;
            public void Exit(int exitCode) => throw new NotImplementedException();
        }
    }

    [Test]
    public void CommitThroughOverridableScope_StoresSnapshotLocally_ReadableWithinOverridableScope()
    {
        using TestContext ctx = new();
        FlatOverridableWorldScope overridableScope = ctx.OverridableScope;

        Address testAddress = TestItem.AddressA;
        Account testAccount = TestItem.GenerateRandomAccount();
        UInt256 storageIndex1 = 42;
        UInt256 storageIndex2 = 100;
        byte[] storageValue1 = [1, 2, 3, 4];
        byte[] storageValue2 = [5, 6, 7, 8, 9, 10];

        // Write account and storage, then commit
        BlockHeader? baseBlock = null;
        using (IWorldStateScopeProvider.IScope scope = overridableScope.WorldState.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(testAddress, testAccount);

                using (IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(testAddress, 2))
                {
                    storageBatch.Set(storageIndex1, storageValue1);
                    storageBatch.Set(storageIndex2, storageValue2);
                }
            }
            scope.Commit(1);
            baseBlock = Build.A.BlockHeader.WithNumber(1).WithStateRoot(scope.RootHash).TestObject;
        }

        // Verify account readable within new scope
        using (IWorldStateScopeProvider.IScope scope = overridableScope.WorldState.BeginScope(baseBlock))
        {
            Account? readAccount = scope.Get(testAddress);
            Assert.That(readAccount, Is.Not.Null);
            Assert.That(readAccount!.Balance, Is.EqualTo(testAccount.Balance));
        }

        // Verify account readable through GlobalStateReader
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(baseBlock, testAddress, out AccountStruct acc), Is.True);
        Assert.That(acc.Balance, Is.EqualTo(testAccount.Balance));

        // Verify storage readable through GlobalStateReader
        ReadOnlySpan<byte> readValue1 = overridableScope.GlobalStateReader.GetStorage(baseBlock, testAddress, storageIndex1);
        ReadOnlySpan<byte> readValue2 = overridableScope.GlobalStateReader.GetStorage(baseBlock, testAddress, storageIndex2);
        Assert.That(readValue1.ToArray(), Is.EqualTo(storageValue1), "Storage slot 1 should be readable");
        Assert.That(readValue2.ToArray(), Is.EqualTo(storageValue2), "Storage slot 2 should be readable");

        // Verify non-existent slot returns zeros
        ReadOnlySpan<byte> nonExistent = overridableScope.GlobalStateReader.GetStorage(baseBlock, testAddress, 999);
        Assert.That(nonExistent.ToArray().All(b => b == 0), Is.True, "Non-existent storage slot should return zeros");
    }

    [Test]
    public void CommitThroughOverridableScope_DoesNotCallMainFlatDbManager()
    {
        using TestContext ctx = new();
        FlatOverridableWorldScope overridableScope = ctx.OverridableScope;

        Address testAddress = TestItem.AddressA;
        Account testAccount = TestItem.GenerateRandomAccount();

        BlockHeader? baseBlock = null;
        using (IWorldStateScopeProvider.IScope scope = overridableScope.WorldState.BeginScope(baseBlock))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(testAddress, testAccount);
            }
            scope.Commit(1);
        }

        // The main FlatDbManager should NOT receive any AddSnapshot calls
        // because commits go to FlatOverridableWorldScope's local _snapshots dictionary
        Assert.That(ctx.FlatDbManagerAddSnapshotCalls, Is.Empty);
    }

    [Test]
    public void MultipleCommits_CreateChainedSnapshots_AllReadable()
    {
        using TestContext ctx = new();
        FlatOverridableWorldScope overridableScope = ctx.OverridableScope;

        Address addressA = TestItem.AddressA;
        Address addressB = TestItem.AddressB;
        Address addressC = TestItem.AddressC;
        Account accountA = TestItem.GenerateRandomAccount();
        Account accountB = TestItem.GenerateRandomAccount();
        Account accountC = TestItem.GenerateRandomAccount();

        // Commit block 1 with account A
        BlockHeader? block1 = null;
        using (IWorldStateScopeProvider.IScope scope = overridableScope.WorldState.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(addressA, accountA);
            }
            scope.Commit(1);
            block1 = Build.A.BlockHeader.WithNumber(1).WithStateRoot(scope.RootHash).TestObject;
        }

        // Commit block 2 with account B (building on block 1)
        BlockHeader? block2 = null;
        using (IWorldStateScopeProvider.IScope scope = overridableScope.WorldState.BeginScope(block1))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(addressB, accountB);
            }
            scope.Commit(2);
            block2 = Build.A.BlockHeader.WithNumber(2).WithStateRoot(scope.RootHash).TestObject;
        }

        // Commit block 3 with account C (building on block 2)
        BlockHeader? block3 = null;
        using (IWorldStateScopeProvider.IScope scope = overridableScope.WorldState.BeginScope(block2))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(addressC, accountC);
            }
            scope.Commit(3);
            block3 = Build.A.BlockHeader.WithNumber(3).WithStateRoot(scope.RootHash).TestObject;
        }

        // Verify final state (block 3) sees all three accounts
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block3, addressA, out AccountStruct accA3), Is.True, "Block 3 should see account A");
        Assert.That(accA3.Balance, Is.EqualTo(accountA.Balance));
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block3, addressB, out AccountStruct accB3), Is.True, "Block 3 should see account B");
        Assert.That(accB3.Balance, Is.EqualTo(accountB.Balance));
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block3, addressC, out AccountStruct accC3), Is.True, "Block 3 should see account C");
        Assert.That(accC3.Balance, Is.EqualTo(accountC.Balance));

        // Verify intermediate state (block 2) sees A+B but not C
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block2, addressA, out AccountStruct accA2), Is.True, "Block 2 should see account A");
        Assert.That(accA2.Balance, Is.EqualTo(accountA.Balance));
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block2, addressB, out AccountStruct accB2), Is.True, "Block 2 should see account B");
        Assert.That(accB2.Balance, Is.EqualTo(accountB.Balance));
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block2, addressC, out _), Is.False, "Block 2 should NOT see account C");

        // Verify initial state (block 1) sees only A
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block1, addressA, out AccountStruct accA1), Is.True, "Block 1 should see account A");
        Assert.That(accA1.Balance, Is.EqualTo(accountA.Balance));
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block1, addressB, out _), Is.False, "Block 1 should NOT see account B");
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block1, addressC, out _), Is.False, "Block 1 should NOT see account C");

        // Verify no calls to main FlatDbManager
        Assert.That(ctx.FlatDbManagerAddSnapshotCalls, Is.Empty);
    }

    [Test]
    public void ResetOverrides_DisposesAllLocalSnapshots()
    {
        using TestContext ctx = new();
        FlatOverridableWorldScope overridableScope = ctx.OverridableScope;

        Address testAddress = TestItem.AddressA;
        Account testAccount = TestItem.GenerateRandomAccount();

        // Commit multiple states
        BlockHeader? block1 = null;
        using (IWorldStateScopeProvider.IScope scope = overridableScope.WorldState.BeginScope(null))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
            {
                writeBatch.Set(testAddress, testAccount);
            }
            scope.Commit(1);
            block1 = Build.A.BlockHeader.WithNumber(1).WithStateRoot(scope.RootHash).TestObject;
        }

        // Verify state exists before reset
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block1, testAddress, out _), Is.True, "Should see account before reset");

        // Reset overrides
        overridableScope.ResetOverrides();

        // After reset, the local snapshots are cleared, so state falls through to main FlatDbManager
        // which is mocked to return empty/not found
        Assert.That(overridableScope.GlobalStateReader.TryGetAccount(block1, testAddress, out _), Is.False, "Should NOT see account after reset");
    }
}
