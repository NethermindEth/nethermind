// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatStateReaderTests
{
    private static readonly BlockHeader _header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject;

    private static FlatStateReader CreateReader(IFlatDbManager manager) =>
        new(new MemDb(), manager, LimboLogs.Instance);

    public static readonly TestCaseData[] UnavailableStateReads =
    [
        new TestCaseData((Action<FlatStateReader>)(reader => reader.TryGetAccount(_header, TestItem.AddressA, out _))) { TestName = "TryGetAccount" },
        new TestCaseData((Action<FlatStateReader>)(reader => reader.GetStorage(_header, TestItem.AddressA, 1))) { TestName = "GetStorage" },
        new TestCaseData((Action<FlatStateReader>)(reader => reader.RunTreeVisitor(new TreeDumper(), _header))) { TestName = "RunTreeVisitor" },
    ];

    [TestCaseSource(nameof(UnavailableStateReads))]
    public void Read_WhenStateUnavailable_ThrowsMissingTrieNodeException(Action<FlatStateReader> read)
    {
        FlatStateReader reader = CreateReader(new ThrowingFlatDbManager());

        Assert.Throws<MissingTrieNodeException>(() => read(reader));
    }

    [Test]
    public void RunTreeVisitor_OnHistoricalBundle_ThrowsMissingTrieNodeException()
    {
        FlatStateReader reader = CreateReader(new HistoricalBundleFlatDbManager());

        MissingTrieNodeException? exception = Assert.Throws<MissingTrieNodeException>(() => reader.RunTreeVisitor(new TreeDumper(), _header));
        Assert.That(exception!.Message, Does.Contain("historical"));
    }

    private class ThrowingFlatDbManager : IFlatDbManager
    {
        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
            throw new StateUnavailableException($"State {baseBlock} no longer exists; concurrently removed.");

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public bool HasStateForBlock(in StateId stateId) => false;
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) { }
    }

    private class HistoricalBundleFlatDbManager : IFlatDbManager
    {
        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
            new(new SnapshotPooledList(0), new NoopPersistenceReader(), false, PersistedSnapshotStack.Empty(false), isHistorical: true);

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public bool HasStateForBlock(in StateId stateId) => true;
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) { }
    }
}
