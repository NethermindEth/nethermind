// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtCachedReaderPersistenceTests
{
    private static readonly StateId _committedState = new(1, TestItem.KeccakA);
    private static readonly ValueHash256 _committedTreeRoot = TestItem.KeccakB;

    /// <summary>
    /// The point of the cache: everyone reading the same persisted state gets the same snapshot, and
    /// the commit that makes it stale is what takes it away.
    /// </summary>
    [Test]
    public async Task Readers_ShareOneSnapshot_UntilAWriteBatchCommits()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        using IPbtPersistence.IReader first = persistence.CreateReader();
        using IPbtPersistence.IReader second = persistence.CreateReader();

        Assert.That(second, Is.SameAs(first));
        ctx.Inner.Received(1).CreateReader();

        persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedTreeRoot, WriteFlags.None).Dispose();

        using IPbtPersistence.IReader afterCommit = persistence.CreateReader();

        Assert.That(afterCommit, Is.Not.SameAs(first));
        ctx.Inner.Received(2).CreateReader();
    }

    /// <summary>The snapshot outlives the cache entry: whoever is still reading it keeps it open.</summary>
    [Test]
    public async Task Snapshot_IsClosed_OnlyOnceTheCacheAndEveryReaderReleasedIt()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IReader stillReading = persistence.CreateReader();
        persistence.CreateReader().Dispose();

        ctx.Reader.DidNotReceive().Dispose();

        persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedTreeRoot, WriteFlags.None).Dispose();

        ctx.Reader.DidNotReceive().Dispose();

        stillReading.Dispose();

        ctx.Reader.Received(1).Dispose();
    }

    [Test]
    public async Task SharedReader_ForwardsToTheSnapshotUnderneath()
    {
        Context ctx = new();
        Stem stem = PbtKeyDerivation.AccountHeaderStem(TestItem.AddressA);
        RefCountingMemory blob = RefCountingMemory.Wrapping([0x11]);
        ctx.Reader.CurrentState.Returns(_committedState);
        ctx.Reader.CurrentTreeRoot.Returns(_committedTreeRoot);
        ctx.Reader.GetLeafBlob(stem).Returns(blob);

        await using PbtCachedReaderPersistence persistence = ctx.Build();
        using IPbtPersistence.IReader reader = persistence.CreateReader();

        Assert.That(reader.CurrentState, Is.EqualTo(_committedState));
        Assert.That(reader.CurrentTreeRoot, Is.EqualTo(_committedTreeRoot));
        Assert.That(reader.GetLeafBlob(stem), Is.SameAs(blob));
    }

    private sealed class Context
    {
        public IPbtPersistence Inner { get; } = Substitute.For<IPbtPersistence>();

        public IPbtPersistence.IReader Reader { get; } = Substitute.For<IPbtPersistence.IReader>();

        public Context()
        {
            Inner.CreateReader().Returns(Reader, Substitute.For<IPbtPersistence.IReader>());
            Inner.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
                .Returns(Substitute.For<IPbtPersistence.IWriteBatch>());
        }

        public PbtCachedReaderPersistence Build() => new(Inner, Substitute.For<IProcessExitSource>());
    }
}
