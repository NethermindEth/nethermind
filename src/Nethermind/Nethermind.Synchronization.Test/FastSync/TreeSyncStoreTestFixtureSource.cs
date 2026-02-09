// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Autofac;
using Autofac.Features.AttributeFilters;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class TreeSyncStoreTestFixtureSource : IEnumerable
{
    public static void RegisterPatriciaStore(ContainerBuilder builder) => builder
        .AddSingleton<ITreeSyncStore, PatriciaTreeSyncStore>()
        .AddSingleton<ITestOperation, LocalDbContext>()
        ;

    // Future:
    // public static void RegisterFlatStore(ContainerBuilder builder) =>
    //     builder.Register(ctx => new FlatTreeSyncStore(...)).As<ITreeSyncStore>().SingleInstance();

    public IEnumerator GetEnumerator()
    {
        yield return new TestFixtureData((Action<ContainerBuilder>)RegisterPatriciaStore)
            .SetArgDisplayNames("Patricia");
        // Future: yield return for Flat
    }

    public interface ITestOperation
    {
        // Add here
    }
}

public class LocalDbContext: TreeSyncStoreTestFixtureSource.ITestOperation
{
    public LocalDbContext(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        [KeyFilter(DbNames.State)] IDb stateDb,
        INodeStorage nodeStorage,
        ILogManager logManager)
    {
        NodeStorage = nodeStorage;
        CodeDb = (TestMemDb)codeDb;
        Db = (TestMemDb)stateDb;
        StateTree = new StateTree(TestTrieStoreFactory.Build(nodeStorage, logManager), logManager);
    }

    private TestMemDb CodeDb { get; }
    private TestMemDb Db { get; }
    private INodeStorage NodeStorage { get; }
    private StateTree StateTree { get; }

    public Hash256 RootHash
    {
        get => StateTree.RootHash;
        set => StateTree.RootHash = value;
    }

    public void UpdateRootHash() => StateTree.UpdateRootHash();

    public void Set(Hash256 address, Account? account) => StateTree.Set(address, account);

    public void Commit() => StateTree.Commit();

    public void AssertFlushed()
    {
        Db.WasFlushed.Should().BeTrue();
        CodeDb.WasFlushed.Should().BeTrue();
    }

    public void CompareTrees(RemoteDbContext remote, ILogger logger, string stage, bool skipLogs = false)
    {
        if (!skipLogs) logger.Info($"==================== {stage} ====================");
        StateTree.RootHash = remote.StateTree.RootHash;

        if (!skipLogs) logger.Info("-------------------- REMOTE --------------------");
        TreeDumper dumper = new TreeDumper();
        remote.StateTree.Accept(dumper, remote.StateTree.RootHash);
        string remoteStr = dumper.ToString();
        if (!skipLogs) logger.Info(remoteStr);
        if (!skipLogs) logger.Info("-------------------- LOCAL --------------------");
        dumper.Reset();
        StateTree.Accept(dumper, StateTree.RootHash);
        string localStr = dumper.ToString();
        if (!skipLogs) logger.Info(localStr);

        if (stage == "END")
        {
            Assert.That(localStr, Is.EqualTo(remoteStr), $"{stage}{Environment.NewLine}{remoteStr}{Environment.NewLine}{localStr}");
            TrieStatsCollector collector = new(CodeDb, LimboLogs.Instance);
            StateTree.Accept(collector, StateTree.RootHash);
            Assert.That(collector.Stats.MissingNodes, Is.EqualTo(0));
            Assert.That(collector.Stats.MissingCode, Is.EqualTo(0));
        }
    }

    public void DeleteStateRoot()
    {
        NodeStorage.Set(null, TreePath.Empty, RootHash, null);
    }
}

public class StateSyncFeedTestsFixtureSource : IEnumerable
{
    private static readonly (int peerCount, int maxLatency)[] PeerConfigs =
    [
        (1, 0),
        (1, 100),
        (4, 0),
        (4, 100)
    ];

    public IEnumerator GetEnumerator()
    {
        foreach (var (peerCount, maxLatency) in PeerConfigs)
        {
            yield return new TestFixtureData(
                (Action<ContainerBuilder>)TreeSyncStoreTestFixtureSource.RegisterPatriciaStore,
                peerCount,
                maxLatency
            ).SetArgDisplayNames($"Patricia-{peerCount}peers-{maxLatency}ms");
            // Future: yield return for Flat
        }
    }
}
