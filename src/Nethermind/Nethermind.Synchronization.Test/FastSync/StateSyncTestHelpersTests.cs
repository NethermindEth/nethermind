// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Synchronization.FastSync;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Synchronization.Test.FastSync;

/// <summary>
/// Pins the helpers the state-sync tests rely on: a helper that stops failing turns no other test red.
/// </summary>
[TestFixture]
public class StateSyncTestHelpersTests : StateSyncFeedTestsBase
{
    private static IEnumerable<TestCaseData> UnfinishedRounds()
    {
        yield return new TestCaseData(static () => Task.FromException(new InvalidOperationException("round failed")),
            Throws.InvalidOperationException.With.Message.EqualTo("round failed")).SetArgDisplayNames("faulted");
        // The helper cancels a round only after its timeout, so a round cancelled before that is not a finished one.
        yield return new TestCaseData(static () => Task.FromCanceled(new CancellationToken(canceled: true)),
            Throws.InstanceOf<OperationCanceledException>()).SetArgDisplayNames("cancelled");
    }

    [TestCaseSource(nameof(UnfinishedRounds))]
    public async Task ActivateAndWait_rethrows_a_round_that_did_not_finish(Func<Task> round, IResolveConstraint expected)
    {
        IStateSyncRunner runner = Substitute.For<IStateSyncRunner>();
        runner.RunStateSyncRounds(Arg.Any<CancellationToken>()).Returns(round());
        await using IContainer container = PrepareDownloader(new RemoteDbContext(_logManager),
            configureBuilder: builder => builder.AddSingleton<IStateSyncRunner>(runner));

        await Assert.ThatAsync(() => ActivateAndWait(container.Resolve<SafeContext>()), expected);
    }

    [Test]
    public async Task CompareTrees_fails_on_different_trees_at_any_stage([Values("END", "BEFORE SYNC")] string stage)
    {
        RemoteDbContext remote = new(_logManager);
        remote.StateTree.Set(TestItem.KeccakA, Build.An.Account.TestObject);
        remote.StateTree.Commit();
        await using IContainer container = PrepareDownloader(remote);
        IStateSyncTestOperation local = container.Resolve<IStateSyncTestOperation>();

        // Nothing was synced, so the local tree is empty.
        AssertionException? failure = Assert.Throws<AssertionException>(() => local.CompareTrees(remote, _logger, stage));
        Assert.That(failure, Has.Message.Contain(stage));
    }

    [Test]
    public async Task CompareTrees_fails_on_missing_code_at_any_stage()
    {
        const string stage = "BEFORE SYNC";
        Hash256 codeHash = Keccak.Compute(TrieScenarios.Code0);
        Account contract = TrieScenarios.AccountJustState0.WithChangedCodeHash(codeHash);

        RemoteDbContext remote = new(_logManager);
        remote.CodeDb.Set(codeHash, TrieScenarios.Code0);
        remote.StateTree.Set(TestItem.KeccakA, contract);
        remote.StateTree.Commit();
        await using IContainer container = PrepareDownloader(remote);
        IStateSyncTestOperation local = container.Resolve<IStateSyncTestOperation>();

        // The same trie as the remote one, but the contract's code never reached the local code store.
        local.SetAccountsAndCommit((TestItem.KeccakA, contract));
        Assert.That(local.RootHash, Is.EqualTo(remote.StateTree.RootHash));

        // MissingNodes counts missing code too, so both of the collector's assertions must fail here.
        MultipleAssertException? failure = Assert.Throws<MultipleAssertException>(() => local.CompareTrees(remote, _logger, stage));
        Assert.That(failure, Has.Message.Contain("MissingCode").And.Message.Contain("MissingNodes"));

        // With the code in place, the same comparison passes: the failure above was about the code only.
        container.ResolveNamed<IDb>(DbNames.Code).Set(codeHash, TrieScenarios.Code0);
        local.CompareTrees(remote, _logger, stage);
    }
}
