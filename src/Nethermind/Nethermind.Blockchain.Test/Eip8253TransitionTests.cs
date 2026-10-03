// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Config;
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
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>Block-level tests of the EIP-8253 fork-block nonce bump, through block production and validation.</summary>
[Parallelizable(ParallelScope.All)]
public class Eip8253TransitionTests
{
    private const ulong ForkBlockNumber = 2;

    // On Mainnet 0x8398…8462 was created by 0x9ca2…9f79 at nonce 0, so a factory placed there collides with it.
    private static readonly Address Target = Eip8253Constants.MainnetAccounts[5];
    private static readonly Address TargetCreator = new("0x9ca228250f9d8f86c23690074c2b96d5f5479f79");
    private static readonly Address AbsentTarget = Eip8253Constants.MainnetAccounts[0];
    private static readonly Address LookAlike = TestItem.AddressF;
    private static readonly UInt256 TargetBalance = 1.Ether;
    private static readonly UInt256 StoredValue = 0xabcdef;

    // PUSH1 0, PUSH1 0, PUSH1 0, CREATE, PUSH1 0, SSTORE: stores the created address, or zero on a collision.
    private static readonly byte[] CreateAndStoreResult = [0x60, 0x00, 0x60, 0x00, 0x60, 0x00, 0xf0, 0x60, 0x00, 0x55];

    [Test]
    public async Task Fork_block_bumps_every_listed_account_before_its_transactions([Values] bool parallelExecution)
    {
        using BasicTestBlockchain chain = await CreateChain(BlockchainIds.Mainnet, ForkBlockNumber, parallelExecution);
        Assert.That(ContractAddress.From(TargetCreator, 0), Is.EqualTo(Target), "precondition: the factory's CREATE derives the target");

        Block preFork = await chain.AddBlock();
        Block fork = await chain.AddBlock(
            Transfer(0, TargetCreator, UInt256.Zero, gasLimit: 1_000_000),
            Transfer(1, Target, UInt256.One),
            Transfer(2, LookAlike, UInt256.One));
        Block postFork = await chain.AddBlock(Transfer(3, TestItem.AddressC, UInt256.One));

        Assert.That(fork.Number, Is.EqualTo(ForkBlockNumber), "precondition: the transactions landed in the fork block");
        TxReceipt[] receipts = chain.ReceiptStorage.Get(fork);
        ReadOnlyBlockAccessList forkBal = GetBlockAccessList(chain, fork);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipts.Select(static r => r.StatusCode), Is.All.EqualTo(StatusCode.Success));
            Assert.That(GetStorage(chain, fork, TargetCreator, UInt256.Zero), Is.EqualTo(UInt256.Zero), "CREATE to the bumped target collides under EIP-684");
            Assert.That(receipts[1].GasUsed, Is.EqualTo(receipts[2].GasUsed), "a call to the target costs the same as to any existing account");

            AssertAccount(chain, preFork, Target, nonce: 0, TargetBalance, StoredValue);
            AssertAccount(chain, fork, Target, nonce: 1, TargetBalance + 1, StoredValue);
            AssertAccount(chain, fork, LookAlike, nonce: 0, TargetBalance + 1, StoredValue);
            AssertAccount(chain, fork, AbsentTarget, nonce: 1, UInt256.Zero, UInt256.Zero);

            AssertOnlyBump(forkBal.GetAccountChanges(Target), balanceChangeIndex: 2);
            AssertOnlyBump(forkBal.GetAccountChanges(AbsentTarget), balanceChangeIndex: null);
            Assert.That(Eip8253Constants.MainnetAccounts.Select(a => forkBal.GetAccountChanges(a)?.NonceChanges),
                Is.All.EqualTo(new[] { new NonceChange(0, 1) }), "every listed account records the bump at index 0");
            Assert.That(forkBal.GetAccountChanges(LookAlike)!.NonceChanges, Is.Empty, "an unlisted account of the same shape is not bumped");

            Assert.That(GetBlockAccessList(chain, preFork).GetAccountChanges(Target), Is.Null, "no bump before the fork block");
            Assert.That(GetBlockAccessList(chain, postFork).GetAccountChanges(Target), Is.Null, "no bump after the fork block");
        }
    }

    [Test]
    public async Task Fork_block_validation_requires_the_bump_in_the_block_access_list([Values] bool parallelExecution, [Values] bool dropBump)
    {
        using BasicTestBlockchain chain = await CreateChain(BlockchainIds.Mainnet, ForkBlockNumber, parallelExecution);
        await chain.AddBlock();
        Block fork = await chain.AddBlock(Transfer(0, Target, UInt256.One));
        ReadOnlyBlockAccessList bal = GetBlockAccessList(chain, fork);

        Block suggested = fork.WithReplacedHeader(fork.Header.Clone());
        suggested.BlockAccessList = dropBump ? WithoutNonceChanges(bal, Target) : bal;
        IBlockAccessListManager balManager = ((MainProcessingContext)chain.MainProcessingContext).LifetimeScope.Resolve<IBlockAccessListManager>();

        using IDisposable scope = chain.MainWorldState.BeginScope(chain.BlockTree.FindHeader(fork.ParentHash!, BlockTreeLookupOptions.None));
        Action process = () => chain.BlockProcessor.ProcessOne(
            suggested, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(fork.Header), CancellationToken.None);

        Assert.That(process, dropBump ? Throws.InstanceOf<InvalidBlockException>().With.Message.Contains("InvalidBlockLevelAccessList") : Throws.Nothing);
        Assert.That(balManager.ParallelExecutionEnabled, Is.EqualTo(parallelExecution), "the suggested BAL drives the requested execution path");
    }

    [TestCase(BlockchainIds.Mainnet, 0ul, TestName = "Transition_skipped_when_parent_already_has_the_fork")]
    [TestCase(BlockchainIds.Sepolia, ForkBlockNumber, TestName = "Transition_skipped_on_a_chain_without_a_list")]
    public async Task Transition_skipped(ulong chainId, ulong forkBlockNumber)
    {
        using BasicTestBlockchain chain = await CreateChain(chainId, forkBlockNumber, parallelExecution: true);

        await chain.AddBlock();
        Block block = await chain.AddBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Number, Is.EqualTo(ForkBlockNumber));
            AssertAccount(chain, block, Target, nonce: 0, TargetBalance, StoredValue);
            Assert.That(chain.StateReader.TryGetAccount(block.Header, AbsentTarget, out _), Is.False);
            Assert.That(GetBlockAccessList(chain, block).GetAccountChanges(Target), Is.Null);
        }
    }

    [Test]
    public void Parent_is_looked_up_only_until_a_processed_block_shows_the_fork_is_active([Values] bool producing)
    {
        IReleaseSpec eip8253 = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8253Enabled = true };
        TestSpecProvider specProvider = new(eip8253) { NextForkSpec = eip8253, AllowTestChainOverride = false };
        BlockHeader[] chain = new BlockHeader[4];
        for (int i = 0; i < chain.Length; i++)
        {
            chain[i] = (i == 0 ? Build.A.BlockHeader.WithNumber(0) : Build.A.BlockHeader.WithParent(chain[i - 1])).TestObject;
        }

        IHeaderFinder headerFinder = Substitute.For<IHeaderFinder>();
        headerFinder.Get(chain[0].Hash!, 0).Returns(chain[0]);
        IWorldState state = Substitute.For<IWorldState>();
        ZeroNonceStorageAccountsTransition transition = new(specProvider, headerFinder);

        for (int i = 1; i < chain.Length; i++)
        {
            // A block being built only gets its hash once processing ends.
            Hash256 hash = chain[i].Hash!;
            if (producing) chain[i].Hash = null;
            transition.ApplyIfForkBlock(chain[i], eip8253, state);
            chain[i].Hash = hash;
        }

        headerFinder.ReceivedWithAnyArgs(1).Get(default!, default);
        state.DidNotReceiveWithAnyArgs().SetNonce(default!, default);
    }

    /// <remarks>The asset lists 28 accounts ordered by address hash, so a mistyped address breaks the order.</remarks>
    [Test]
    public void Mainnet_list_has_the_published_accounts_in_address_hash_order()
    {
        IReadOnlyList<Address> accounts = Eip8253Constants.MainnetAccounts;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts, Has.Count.EqualTo(28));
            Assert.That(accounts.Select(static a => Keccak.Compute(a.Bytes).ToString()), Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
            Assert.That(Eip8253Constants.GetAccounts(BlockchainIds.Mainnet), Is.SameAs(accounts));
            Assert.That(Eip8253Constants.GetAccounts(BlockchainIds.Sepolia), Is.Empty);
        }
    }

    private static async Task<BasicTestBlockchain> CreateChain(ulong chainId, ulong forkBlockNumber, bool parallelExecution)
    {
        IReleaseSpec eip8253 = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8253Enabled = true };
        TestSpecProvider specProvider = new(forkBlockNumber == 0 ? eip8253 : Amsterdam.Instance)
        {
            NextForkSpec = eip8253,
            ForkOnBlockNumber = forkBlockNumber == 0 ? null : forkBlockNumber,
            ChainId = chainId,
            AllowTestChainOverride = false,
        };

        return await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(specProvider)
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = parallelExecution)
            .WithGenesisPostProcessor((_, state, spec) =>
            {
                foreach (Address address in (Address[])[Target, LookAlike])
                {
                    state.CreateAccount(address, TargetBalance);
                    state.Set(new StorageCell(address, UInt256.One), StoredValue);
                }

                state.CreateAccount(TargetCreator, UInt256.Zero);
                state.InsertCode(TargetCreator, CreateAndStoreResult, spec.GenesisSpec);
                state.Set(new StorageCell(TargetCreator, UInt256.Zero), UInt256.One);
            }));
    }

    private static Transaction Transfer(ulong nonce, Address to, UInt256 value, ulong gasLimit = 100_000) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(to).WithValue(value).WithGasLimit(gasLimit)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

    private static ReadOnlyBlockAccessList GetBlockAccessList(BasicTestBlockchain chain, Block block) =>
        chain.Container.Resolve<IBlockAccessListStore>().Get(block.Number, block.Hash!)
        ?? throw new AssertionException($"block {block.Number} has no stored block access list");

    private static ReadOnlyBlockAccessList WithoutNonceChanges(ReadOnlyBlockAccessList bal, Address address) =>
        Build.A.BlockAccessList.WithAccountChanges([.. bal.AccountChanges.AsSpan().ToArray().Select(a => a.Address == address
            ? new ReadOnlyAccountChanges(a.Address, a.StorageChanges, a.StorageReads, a.BalanceChanges, [], a.CodeChanges)
            : a)]).TestObject;

    private static UInt256 GetStorage(BasicTestBlockchain chain, Block block, Address address, in UInt256 index)
    {
        chain.StateReader.GetStorage(block.Header, address, index, out UInt256 value);
        return value;
    }

    private static void AssertAccount(BasicTestBlockchain chain, Block block, Address address, ulong nonce, UInt256 balance, UInt256 slotOne)
    {
        Assert.That(chain.StateReader.TryGetAccount(block.Header, address, out AccountStruct account), Is.True, $"{address} exists at block {block.Number}");
        Assert.That(account.Nonce, Is.EqualTo(nonce), $"{address} nonce at block {block.Number}");
        Assert.That(account.Balance, Is.EqualTo(balance), $"{address} balance at block {block.Number}");
        Assert.That(account.IsContract, Is.False, $"{address} code at block {block.Number}");
        Assert.That(GetStorage(chain, block, address, UInt256.One), Is.EqualTo(slotOne), $"{address} storage at block {block.Number}");
    }

    private static void AssertOnlyBump(ReadOnlyAccountChanges? changes, uint? balanceChangeIndex)
    {
        Assert.That(changes, Is.Not.Null);
        Assert.That(changes!.NonceChanges, Is.EqualTo(new[] { new NonceChange(0, 1) }));
        Assert.That(changes.BalanceChanges.Select(static c => c.Index), Is.EqualTo(balanceChangeIndex is uint index ? new[] { index } : []));
        Assert.That(changes.CodeChanges, Is.Empty);
        Assert.That(changes.StorageChanges, Is.Empty);
        Assert.That(changes.StorageReads, Is.Empty);
    }
}
