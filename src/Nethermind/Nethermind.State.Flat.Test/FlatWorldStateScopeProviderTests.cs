// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using NSubstitute;
using NUnit.Framework;
using RocksDbSharp;
using ZstdSharp.Unsafe;

namespace Nethermind.State.Flat.Test;

public class FlatWorldStateScopeProviderTests
{

    private class TestContext: IDisposable
    {
        private readonly ContainerBuilder _containerBuilder;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private IContainer? _container;
        private IContainer Container => _container ??= _containerBuilder.Build();

        public ResourcePool ResourcePool => field ??= Container.Resolve<ResourcePool>();
        public ArrayPoolList<Snapshot> ReadOnlySnapshots => field ??= Container.Resolve<ArrayPoolList<Snapshot>>();
        public IPersistence.IPersistenceReader PersistenceReader => field ??= Container.Resolve<IPersistence.IPersistenceReader>();

        public TestContext(FlatDbConfig? config = null)
        {
            config ??= new FlatDbConfig();

            _containerBuilder = new ContainerBuilder()
                    .AddModule(new FlatWorldStateModule(config))
                    .AddSingleton<IPersistence.IPersistenceReader>(_ => Substitute.For<IPersistence.IPersistenceReader>())
                    .AddSingleton<IFlatDiffRepository>((ctx) =>
                    {
                        ResourcePool resourcePool = ctx.Resolve<ResourcePool>();
                        IFlatDiffRepository flatDiff = Substitute.For<IFlatDiffRepository>();
                        flatDiff.When(it => it.AddSnapshot(Arg.Any<Snapshot>(), Arg.Any<CachedResource>()))
                            .Do(c =>
                            {
                                Snapshot snapshot = (Snapshot)c[0];
                                CachedResource cachedResource = (CachedResource)c[1];

                                resourcePool.ReturnCachedResource(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing, cachedResource);
                                snapshot.Dispose();
                            });

                        return flatDiff;
                    })
                    .AddSingleton<IProcessExitSource>(_ => new CancellationTokenSourceProcessExitSource(_cancellationTokenSource))
                    .AddSingleton<ILogManager>(LimboLogs.Instance)
                    .AddSingleton<IFlatDbConfig>(config)
                    .AddSingleton<ArrayPoolList<Snapshot>>((_) => new ArrayPoolList<Snapshot>(1))
                    .AddSingleton<IWorldStateScopeProvider.ICodeDb>(_ => new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new TestMemDb()))
                ;

            // Externally owned because snapshot bundle take ownership
            _containerBuilder.RegisterType<ReadOnlySnapshotBundle>().ExternallyOwned();

            ConfigureSnapshotBundle();
            ConfigureFlatWorldStateScope();
        }

        private void ConfigureSnapshotBundle()
        {
            _containerBuilder.RegisterType<SnapshotBundle>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing))
                ;
        }

        private void ConfigureFlatWorldStateScope()
        {
            _containerBuilder.RegisterType<FlatWorldStateScope>()
                .SingleInstance()
                .WithParameter(TypedParameter.From(new StateId(0, Keccak.EmptyTreeHash)))
                ;
        }

        public FlatWorldStateScope Scope => Container.Resolve<FlatWorldStateScope>();

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _container?.Dispose();
            _cancellationTokenSource.Dispose();
        }

        public class CancellationTokenSourceProcessExitSource(CancellationTokenSource cancellationTokenSource) : IProcessExitSource
        {
            public CancellationToken Token => cancellationTokenSource.Token;

            public void Exit(int exitCode)
            {
                throw new NotImplementedException();
            }
        }

        public void AddSnapshot(Action<SnapshotContent> populator)
        {
            SnapshotContent snapshotContent = ResourcePool.GetSnapshotContent(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing);
            populator(snapshotContent);

            ReadOnlySnapshots.Add(new Snapshot(
                StateId.PreGenesis,
                StateId.PreGenesis,
                snapshotContent,
                ResourcePool,
                IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing));
        }
    }


    [Test]
    public void TestGetAccountFromReadonlySnapshot()
    {
        using TestContext ctx = new TestContext();

        Account testAccount = TestItem.GenerateRandomAccount();
        ctx.AddSnapshot(content => content.Accounts[TestItem.AddressA] = testAccount);

        Assert.That(ctx.Scope.Get(TestItem.AddressA), Is.EqualTo(testAccount));
    }

    [Test]
    public void TestGetAccountFromPersistenceReader()
    {
        using TestContext ctx = new TestContext();

        Account testAccount = TestItem.GenerateRandomAccount();
        ctx.PersistenceReader.GetAccount(TestItem.AddressA).Returns(testAccount);

        Assert.That(ctx.Scope.Get(TestItem.AddressA), Is.EqualTo(testAccount));
    }

    [Test]
    public void TestGetAccountFromWrittenAccount()
    {
        using TestContext ctx = new TestContext();
        FlatWorldStateScope scope = ctx.Scope;

        Account testAccount = TestItem.GenerateRandomAccount();
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(1))
        {
            writeBatch.Set(TestItem.AddressA, testAccount);
        }

        Assert.That(ctx.Scope.Get(TestItem.AddressA), Is.EqualTo(testAccount));

        // After commit check
        scope.Commit(1);

        Assert.That(ctx.Scope.Get(TestItem.AddressA), Is.EqualTo(testAccount));
    }

}
