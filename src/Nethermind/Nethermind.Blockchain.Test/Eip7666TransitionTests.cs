// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>Block-level tests of the EIP-7666 fork-block code install at 0x04, through block production and validation.</summary>
[Parallelizable(ParallelScope.All)]
public class Eip7666TransitionTests
{
    private const ulong ForkBlockNumber = 2;
    private const ulong OccupiedNonce = 7;

    private static readonly Address Identity = Eip7666Constants.IdentityAddress;
    private static readonly UInt256 OccupiedBalance = 1.Ether;
    private static readonly UInt256 StoredValue = 0xabcdef;
    private static readonly byte[] OccupiedCode = [0x00];
    private static readonly byte[] Input = [1, 2, 3];

    /// <param name="parallelExecution">Whether the fork block is validated through the BAL-driven parallel path.</param>
    /// <param name="occupied">Whether 0x04 already holds a nonce, balance, storage and code before the fork.</param>
    [Test]
    public async Task Fork_block_installs_the_code_before_its_transactions([Values] bool parallelExecution, [Values] bool occupied)
    {
        using BasicTestBlockchain chain = await CreateChain(ForkBlockNumber, parallelExecution, occupied);

        Block preFork = await chain.AddBlock();
        Block fork = await chain.AddBlock(CallIdentity(0));
        Block postFork = await chain.AddBlock(CallIdentity(1));

        Assert.That(fork.Number, Is.EqualTo(ForkBlockNumber), "precondition: the call landed in the fork block");
        TxReceipt[] receipts = chain.ReceiptStorage.Get(fork);
        ReadOnlyAccountChanges? forkChanges = GetBlockAccessList(chain, fork).GetAccountChanges(Identity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipts.Select(static r => r.StatusCode), Is.All.EqualTo(StatusCode.Success));
            Assert.That(chain.StateReader.GetCode(preFork.Header, Identity), Is.EqualTo(occupied ? OccupiedCode : Array.Empty<byte>()), "pre-fork code");
            Assert.That(chain.StateReader.GetCode(fork.Header, Identity), Is.EqualTo(Eip7666Constants.IdentityCode.ToArray()), "fork-block code");
            AssertKeptFields(chain, fork, occupied);

            Assert.That(forkChanges, Is.Not.Null, "0x04 is in the fork block's access list");
            Assert.That(forkChanges!.CodeChanges, Is.EqualTo(new[] { new CodeChange(0, Eip7666Constants.IdentityCode.ToArray()) }), "code change at index 0");
            Assert.That(forkChanges.NonceChanges, Is.Empty, "nonce untouched");
            Assert.That(forkChanges.BalanceChanges, Is.Empty, "balance untouched");
            Assert.That(forkChanges.StorageChanges, Is.Empty, "storage untouched");

            Assert.That(GetBlockAccessList(chain, preFork).GetAccountChanges(Identity), Is.Null, "no install before the fork block");
            Assert.That(GetBlockAccessList(chain, postFork).GetAccountChanges(Identity)?.CodeChanges, Is.Null.Or.Empty, "no install after the fork block");
        }
    }

    [Test]
    public async Task Fork_block_validation_requires_the_install_in_the_block_access_list([Values] bool parallelExecution, [Values] bool dropInstall)
    {
        using BasicTestBlockchain chain = await CreateChain(ForkBlockNumber, parallelExecution, occupied: false);
        await chain.AddBlock();
        Block fork = await chain.AddBlock(CallIdentity(0));
        ReadOnlyBlockAccessList bal = GetBlockAccessList(chain, fork);

        Block suggested = fork.WithReplacedHeader(fork.Header.Clone());
        suggested.BlockAccessList = dropInstall ? WithoutCodeChanges(bal, Identity) : bal;
        IBlockAccessListManager balManager = ((MainProcessingContext)chain.MainProcessingContext).LifetimeScope.Resolve<IBlockAccessListManager>();

        using IDisposable scope = chain.MainWorldState.BeginScope(chain.BlockTree.FindHeader(fork.ParentHash!, BlockTreeLookupOptions.None));
        Action process = () => chain.BlockProcessor.ProcessOne(
            suggested, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(fork.Header), CancellationToken.None);

        Assert.That(process, dropInstall ? Throws.InstanceOf<InvalidBlockException>().With.Message.Contains("InvalidBlockLevelAccessList") : Throws.Nothing);
        Assert.That(balManager.ParallelExecutionEnabled, Is.EqualTo(parallelExecution), "the suggested BAL drives the requested execution path");
    }

    /// <remarks>A chain activating at genesis carries the code in its genesis allocation, so no block installs it.</remarks>
    [TestCase(0ul, TestName = "Transition_skipped_when_parent_already_has_the_fork")]
    [TestCase(ulong.MaxValue, TestName = "Transition_skipped_while_the_fork_is_inactive")]
    public async Task Transition_skipped(ulong forkBlockNumber)
    {
        using BasicTestBlockchain chain = await CreateChain(forkBlockNumber, parallelExecution: true, occupied: false);

        await chain.AddBlock();
        Block block = await chain.AddBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Number, Is.EqualTo(ForkBlockNumber));
            Assert.That(chain.StateReader.TryGetAccount(block.Header, Identity, out _), Is.False);
            Assert.That(GetBlockAccessList(chain, block).GetAccountChanges(Identity), Is.Null);
        }
    }

    [Test]
    public void Parent_is_looked_up_only_until_a_processed_block_shows_the_fork_is_active([Values] bool producing)
    {
        IReleaseSpec eip7666 = new OverridableReleaseSpec(Bogota.Instance) { IsEip7666Enabled = true };
        TestSpecProvider specProvider = new(eip7666) { NextForkSpec = eip7666, AllowTestChainOverride = false };
        BlockHeader[] chain = new BlockHeader[4];
        for (int i = 0; i < chain.Length; i++)
        {
            chain[i] = (i == 0 ? Build.A.BlockHeader.WithNumber(0) : Build.A.BlockHeader.WithParent(chain[i - 1])).TestObject;
        }

        IHeaderFinder headerFinder = Substitute.For<IHeaderFinder>();
        headerFinder.Get(chain[0].Hash!, 0).Returns(chain[0]);
        IWorldState state = Substitute.For<IWorldState>();
        IdentityPrecompileTransition transition = new(specProvider, headerFinder);

        for (int i = 1; i < chain.Length; i++)
        {
            // A block being built only gets its hash once processing ends.
            Hash256 hash = chain[i].Hash!;
            if (producing) chain[i].Hash = null;
            transition.ApplyIfForkBlock(chain[i], eip7666, state);
            chain[i].Hash = hash;
        }

        headerFinder.ReceivedWithAnyArgs(1).Get(default!, default);
        state.DidNotReceiveWithAnyArgs().InsertCode(default!, default, default, default!, default);
    }

    private static async Task<BasicTestBlockchain> CreateChain(ulong forkBlockNumber, bool parallelExecution, bool occupied)
    {
        IReleaseSpec eip7666 = new OverridableReleaseSpec(Bogota.Instance) { IsEip7666Enabled = true };
        TestSpecProvider specProvider = new(forkBlockNumber == 0 ? eip7666 : Bogota.Instance)
        {
            NextForkSpec = eip7666,
            ForkOnBlockNumber = forkBlockNumber == 0 ? null : forkBlockNumber,
            AllowTestChainOverride = false,
        };

        return await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(specProvider)
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = parallelExecution)
            .WithGenesisPostProcessor((_, state, spec) =>
            {
                if (!occupied) return;

                state.CreateAccount(Identity, OccupiedBalance, OccupiedNonce);
                state.Set(new StorageCell(Identity, UInt256.One), StoredValue);
                state.InsertCode(Identity, OccupiedCode, spec.GenesisSpec);
            }));
    }

    private static Transaction CallIdentity(ulong nonce) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(Identity).WithValue(UInt256.Zero).WithData(Input).WithGasLimit(100_000)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

    private static ReadOnlyBlockAccessList GetBlockAccessList(BasicTestBlockchain chain, Block block) =>
        chain.Container.Resolve<IBlockAccessListStore>().Get(block.Number, block.Hash!)
        ?? throw new AssertionException($"block {block.Number} has no stored block access list");

    private static ReadOnlyBlockAccessList WithoutCodeChanges(ReadOnlyBlockAccessList bal, Address address) =>
        Build.A.BlockAccessList.WithAccountChanges([.. bal.AccountChanges.AsSpan().ToArray().Select(a => a.Address == address
            ? new ReadOnlyAccountChanges(a.Address, a.StorageChanges, a.StorageReads, a.BalanceChanges, a.NonceChanges, [])
            : a)]).TestObject;

    private static void AssertKeptFields(BasicTestBlockchain chain, Block block, bool occupied)
    {
        Assert.That(chain.StateReader.TryGetAccount(block.Header, Identity, out AccountStruct account), Is.True, "0x04 exists");
        Assert.That(account.Nonce, Is.EqualTo(occupied ? OccupiedNonce : 0ul), "nonce kept");
        Assert.That(account.Balance, Is.EqualTo(occupied ? OccupiedBalance : UInt256.Zero), "balance kept");
        chain.StateReader.GetStorage(block.Header, Identity, UInt256.One, out UInt256 slotOne);
        Assert.That(slotOne, Is.EqualTo(occupied ? StoredValue : UInt256.Zero), "storage kept");
    }
}
