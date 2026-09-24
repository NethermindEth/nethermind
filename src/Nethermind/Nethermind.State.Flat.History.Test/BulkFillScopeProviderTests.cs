// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

/// <summary>
/// The bulk replay drives blocks through <c>BranchProcessor</c>, which only ever opens target-aware, so the session
/// has to answer for the block it is about to replay — the one whose parent it stands on.
/// </summary>
public class BulkFillScopeProviderTests
{
    private static readonly FlatDbConfig Config = new();

    [Test]
    public void TargetScope_OpensTheBlockTheSessionStandsOnTheParentOf()
    {
        using Fixture fixture = new();
        BlockHeader target = Build.A.BlockHeader.WithParent(fixture.Anchor).TestObject;

        bool opened = fixture.Provider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope? scope);
        scope?.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Provider.HasStateForTargetBlock(target), Is.True);
            Assert.That(opened, Is.True);
        }
    }

    [TestCase(true, TestName = "TargetScope_RefusesABlockOfAnotherParent")]
    [TestCase(false, TestName = "TargetScope_RefusesABlockOfAnotherHeight")]
    public void TargetScope_RefusesABlockTheSessionCannotReplay(bool otherParent)
    {
        using Fixture fixture = new();
        BlockHeader target = otherParent
            ? Build.A.BlockHeader.WithNumber(fixture.Anchor.Number + 1).WithParentHash(TestItem.KeccakF).TestObject
            : Build.A.BlockHeader.WithParent(fixture.Anchor).WithNumber(fixture.Anchor.Number + 2).TestObject;

        bool opened = fixture.Provider.TryBeginScopeAtTarget(target, new LocalMetrics(), out IWorldStateScopeProvider.IScope? scope);
        scope?.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Provider.HasStateForTargetBlock(target), Is.False);
            Assert.That(opened, Is.False);
            Assert.That(scope, Is.Null);
        }
    }

    /// <summary>A ready session anchored at an empty genesis, with the provider over it.</summary>
    private sealed class Fixture : System.IDisposable
    {
        private readonly MemDb _code = new();
        private readonly ResourcePool _resourcePool = new(Config);
        private readonly BulkFillSession _session;

        public BlockHeader Anchor { get; }
        public BulkFillScopeProvider Provider { get; }

        public Fixture()
        {
            Anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
            _session = new BulkFillSession(new MemDbFactory(), _code, TestItem.KeccakA, Anchor, false);
            _session.ImportGenesis([], CancellationToken.None);
            Provider = new BulkFillScopeProvider(_session, Substitute.For<ITrieNodeCache>(), _resourcePool, Config, LimboLogs.Instance);
        }

        public void Dispose()
        {
            _session.Dispose();
            _code.Dispose();
        }
    }
}
