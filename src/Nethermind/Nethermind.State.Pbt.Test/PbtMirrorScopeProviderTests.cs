// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Pbt.Mirror;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtMirrorScopeProviderTests
{
    private static readonly Address Eoa = TestItem.AddressA;

    /// <summary>
    /// The mirrored run reads every value back in a later block, so any divergence between the two
    /// backends surfaces as a <see cref="PbtMirrorMismatchException"/>; the roots additionally have to
    /// match an unmirrored run's, since mirroring must not perturb the authoritative backend.
    /// </summary>
    [Test]
    public async Task MirroredProcessing_MatchesTheUnmirroredRun_AndAgreesOnEveryReadBack()
    {
        Hash256[] plainRoots = PbtTestContext.RunReferenceBlocks(BuildPatriciaProvider());

        await using PbtTestContext ctx = new();
        Hash256[] mirroredRoots = PbtTestContext.RunReferenceBlocks(
            new PbtMirrorScopeProvider(BuildPatriciaProvider(), ctx.Manager, ctx.ResourcePool, ctx.Config, UnavailableStateHeaderProvider.Instance));

        Assert.That(mirroredRoots, Is.EqualTo(plainRoots));

        // States are keyed by the authoritative root, so both backends persist the same ranges.
        for (int block = 0; block < mirroredRoots.Length; block++)
        {
            Assert.That(ctx.Repository.HasState(new StateId((ulong)(block + 1), mirroredRoots[block])), Is.True,
                $"pbt has no state for block {block + 1}");
        }
    }

    [Test]
    public async Task DivergedRead_Throws([Values] bool divergeOnSlot)
    {
        await using PbtTestContext ctx = new();

        // an authoritative backend that answers where the empty pbt state answers nothing
        IWorldStateScopeProvider.IScope authoritativeScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        authoritativeScope.Get(Eoa).Returns(new Account(1, 100));
        IWorldStateScopeProvider.IStorageTree storageTree = Substitute.For<IWorldStateScopeProvider.IStorageTree>();
        storageTree.When(tree => tree.Get(in Arg.Any<UInt256>(), out Arg.Any<UInt256>())).Do(call => call[1] = (UInt256)0xAB);
        authoritativeScope.CreateStorageTree(Eoa).Returns(storageTree);

        IWorldStateScopeProvider authoritative = Substitute.For<IWorldStateScopeProvider>();
        authoritative.TryBeginScope(null, Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope?>()).Returns(call => call.Succeed(2, authoritativeScope));

        PbtMirrorScopeProvider provider = new(authoritative, ctx.Manager, ctx.ResourcePool, ctx.Config, UnavailableStateHeaderProvider.Instance);
        using IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics());

        PbtMirrorMismatchException? mismatch = divergeOnSlot
            ? Assert.Throws<PbtMirrorMismatchException>(() => scope.CreateStorageTree(Eoa).Get(7))
            : Assert.Throws<PbtMirrorMismatchException>(() => scope.Get(Eoa));

        Assert.That(mismatch!.Message, Does.Contain(Eoa.ToString()));
        Assert.That(mismatch.Message, Does.Contain(divergeOnSlot ? "0xab" : "100"));
    }

    private static TrieStoreScopeProvider BuildPatriciaProvider()
    {
        MemDb stateDb = new();
        return new TrieStoreScopeProvider(TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance), new MemDb(), UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);
    }
}
