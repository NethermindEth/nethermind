// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Test;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class CaptureUnderLongFinalityTests
{
    private static FlatDbConfig BackstopConfig() => new()
    {
        HistoryEnabled = true,
        EnableLongFinality = true,
        CompactSize = 16,
        CompactionOffset = 0,
        MinReorgDepth = 8,
        MaxInMemoryBaseSnapshotCount = 16,
        MaxReorgDepth = 32,
        LongFinalityMaxReorgDepth = 32,
        InlineCompaction = true
    };

    [Test]
    public async Task Capture_follows_sync_with_backstop_persistence()
    {
        await using CaptureFixture fixture = new(BackstopConfig());
        for (ulong block = 1; block <= 200; block++)
        {
            StateId next = fixture.Append(block);
            await fixture.Manager.AddToPersistence(next);
            Assert.That(fixture.CaptureDisabled, Is.False, $"capture disabled at {block}");
        }
        Assert.That(fixture.Writer.LastCapturedBlock, Is.GreaterThan(100ul));
    }

    [Test]
    public async Task Capture_follows_sync_under_production_defaults()
    {
        Dictionary<ulong, Hash256> roots = [];
        FlatDbConfig config = new() { HistoryEnabled = true, CompactionOffset = 0, InlineCompaction = true };
        await using CaptureFixture fixture = new(config, new SyncingFinality(roots));
        for (ulong block = 1; block <= 800; block++)
        {
            StateId next = fixture.Append(block);
            roots[block] = new Hash256(next.StateRoot.Bytes);
            await fixture.Manager.AddToPersistence(next);
            Assert.That(fixture.CaptureDisabled, Is.False, $"capture disabled at {block}");
        }
        Assert.That(fixture.Writer.LastCapturedBlock, Is.GreaterThan(600ul));
    }

    private sealed class SyncingFinality(IReadOnlyDictionary<ulong, Hash256> roots) : IStateHeaderProvider
    {
        public ulong FinalizedBlockNumber => 26_000_000;
        public BlockHeader? FindParentHeader(BlockHeader target) => null;
        public BlockHeader? GetFinalizedHeader(ulong blockNumber) =>
            roots.TryGetValue(blockNumber, out Hash256? root)
                ? Build.A.BlockHeader.WithNumber(blockNumber).WithStateRoot(root).TestObject
                : null;
    }

    private sealed class CaptureFixture : IAsyncDisposable
    {
        public FlatTestContainer Tier { get; }
        public IPersistenceManager Manager { get; }
        public HistoryWriter Writer { get; }
        public bool CaptureDisabled { get; private set; }
        private StateId _previous = new(0, Keccak.EmptyTreeHash);

        public CaptureFixture(FlatDbConfig config, IStateHeaderProvider? finalized = null)
        {
            IPersistence persistence = Substitute.For<IPersistence>();
            IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
            reader.CurrentState.Returns(_previous);
            persistence.CreateReader().Returns(reader);
            persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(Substitute.For<IPersistence.IWriteBatch>());
            Tier = new FlatTestContainer(config, finalizedStateProvider: finalized, configure: builder => builder
                .AddSingleton<IColumnsDb<FlatDbColumns>>(new SnapshotableMemColumnsDb<FlatDbColumns>())
                .AddSingleton<IColumnsDb<FlatHistoryColumns>>(new SnapshotableMemColumnsDb<FlatHistoryColumns>())
                .AddSingleton<IPersistence>(persistence)
                .AddSingleton<IStatePersistenceBarrier>(NullStatePersistenceBarrier.Instance));
            Manager = Tier.Resolve<IPersistenceManager>();
            Writer = Tier.Resolve<HistoryWriter>();
            Writer.CaptureDisabled += () => CaptureDisabled = true;
            Writer.SeedGenesis([new KeyValuePair<Address, Account>(TestItem.AddressA, new Account(0, 1))], _previous.StateRoot);
        }

        public StateId Append(ulong block)
        {
            byte[] root = new byte[32];
            root[0] = (byte)block;
            root[1] = (byte)(block >> 8);
            StateId next = new(block, new ValueHash256(root));
            Add(_previous, next, TestItem.AddressA, new Account(block, (UInt256)(block * 100)));
            _previous = next;
            return next;
        }

        private void Add(StateId parent, StateId next, Address address, Account account)
        {
            Snapshot snapshot = Tier.ResourcePool.CreateSnapshot(parent, next, ResourcePool.Usage.MainBlockProcessing);
            snapshot.Content.Accounts[address] = account;
            Tier.Repository.AddStateId(next);
            if (!Tier.Repository.TryAdd(snapshot, SnapshotTier.InMemoryBase)) snapshot.Dispose();
            Tier.Repository.SetLastCommittedStateId(next);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Tier.Compactor.DisposeAsync(); }
            finally { Tier.Dispose(); }
        }
    }
}
