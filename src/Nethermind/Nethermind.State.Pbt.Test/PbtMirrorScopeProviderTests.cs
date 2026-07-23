// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State.Pbt.Mirror;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtMirrorScopeProviderTests
{
    private static readonly IReleaseSpec Spec = Prague.Instance;

    private static readonly Address Eoa = TestItem.AddressA;
    private static readonly Address Contract = TestItem.AddressB;

    /// <summary>
    /// The mirrored run reads every value back in a later block, so any divergence between the two
    /// backends surfaces as a <see cref="PbtMirrorMismatchException"/>; the roots additionally have to
    /// match an unmirrored run's, since mirroring must not perturb the authoritative backend.
    /// </summary>
    [Test]
    public async Task MirroredProcessing_MatchesTheUnmirroredRun_AndAgreesOnEveryReadBack()
    {
        Hash256[] plainRoots = RunBlocks(BuildPatriciaProvider());

        await using PbtTestContext ctx = new();
        Hash256[] mirroredRoots = RunBlocks(
            new PbtMirrorScopeProvider(BuildPatriciaProvider(), ctx.Manager, ctx.ResourcePool, ctx.Config));

        Assert.That(mirroredRoots, Is.EqualTo(plainRoots));

        // the pbt states are keyed by the authoritative root, which is what lets both backends persist
        // the same ranges
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
        storageTree.Get(in Arg.Any<UInt256>()).Returns([0xAB]);
        authoritativeScope.CreateStorageTree(Eoa).Returns(storageTree);

        IWorldStateScopeProvider authoritative = Substitute.For<IWorldStateScopeProvider>();
        authoritative.BeginScope(null, Arg.Any<LocalMetrics>()).Returns(authoritativeScope);

        PbtMirrorScopeProvider provider = new(authoritative, ctx.Manager, ctx.ResourcePool, ctx.Config);
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
        return new TrieStoreScopeProvider(TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance), new MemDb(), LimboLogs.Instance);
    }

    /// <summary>Processes three blocks, the last of which only reads back what the first two wrote.</summary>
    private static Hash256[] RunBlocks(IWorldStateScopeProvider provider)
    {
        WorldState worldState = new(provider, LimboLogs.Instance);

        // long enough to spill past the account header stem into the content-addressed code zone
        byte[] code = new byte[15000];
        for (int i = 0; i < code.Length; i += 10) code[i] = 0x63; // PUSH4, to exercise the chunk PUSHDATA offsets

        Hash256 root1;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(Eoa, 100, 1);
            worldState.CreateAccount(Contract, 42);
            worldState.InsertCode(Contract, ValueKeccak.Compute(code), code, Spec);
            worldState.Set(new StorageCell(Contract, 5), [0xAB]);       // header-region slot
            worldState.Set(new StorageCell(Contract, 1000), Bytes.FromHexString("0x1234")); // storage-zone slot
            worldState.Commit(Spec);
            worldState.CommitTree(1);
            root1 = worldState.StateRoot;
        }

        BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).WithStateRoot(root1).TestObject;
        Hash256 root2;
        using (worldState.BeginScope(header1))
        {
            // the contract is dirtied by storage alone, so its stored account is only rewritten by the
            // storage-root patch the authoritative write batch applies on dispose
            worldState.Set(new StorageCell(Contract, 5), [0]);
            worldState.Set(new StorageCell(Contract, 70), [0x07]);
            worldState.AddToBalance(Eoa, 5, Spec, out _);
            worldState.Commit(Spec);
            worldState.CommitTree(2);
            root2 = worldState.StateRoot;
        }

        BlockHeader header2 = Build.A.BlockHeader.WithNumber(2).WithStateRoot(root2).TestObject;
        Hash256 root3;
        using (worldState.BeginScope(header2))
        {
            Assert.That(worldState.GetBalance(Eoa), Is.EqualTo((UInt256)105));
            Assert.That(worldState.GetBalance(Contract), Is.EqualTo((UInt256)42));
            Assert.That(worldState.GetCode(Contract), Is.EqualTo(code));
            Assert.That(worldState.Get(new StorageCell(Contract, 5)).ToArray(), Is.EqualTo(StorageTree.ZeroBytes));
            Assert.That(worldState.Get(new StorageCell(Contract, 70)).ToArray(), Is.EqualTo((byte[])[0x07]));
            Assert.That(worldState.Get(new StorageCell(Contract, 1000)).ToArray(), Is.EqualTo(Bytes.FromHexString("0x1234")));

            worldState.IncrementNonce(Eoa, 1, out _);
            worldState.Commit(Spec);
            worldState.CommitTree(3);
            root3 = worldState.StateRoot;
        }

        return [root1, root2, root3];
    }
}
