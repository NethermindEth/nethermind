// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// A block access list omits whatever makes its block invalid, so parallel execution against it stops at an undeclared
/// access. The block must still be rejected for the reason sequential execution gives.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class ParallelBalRejectionReasonTests
{
    public enum SenderCase
    {
        Unfunded,
        NonceTooHigh,
        Valid,
    }

    [Test]
    public async Task Block_whose_list_omits_the_sender_reports_the_sequential_rejection(
        [Values] SenderCase senderCase,
        [Values] bool parallelExecution)
    {
        using BasicTestBlockchain chain = await CreateChain(parallelExecution);
        Block produced = await chain.AddBlock();
        ReadOnlyBlockAccessList list = chain.Container.Resolve<IBlockAccessListStore>().Get(produced.Number, produced.Hash!)!;

        Transaction tx = Build.A.Transaction
            .WithNonce(senderCase == SenderCase.NonceTooHigh ? 100UL : 0UL)
            .WithValue(1.Ether)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithType(TxType.EIP1559)
            .WithChainId(TestBlockchainIds.ChainId)
            .To(TestItem.AddressB)
            .SignedAndResolved(senderCase == SenderCase.Unfunded ? TestItem.PrivateKeyF : TestItem.PrivateKeyA)
            .TestObject;

        BlockHeader header = produced.Header.Clone();
        header.TxRoot = TxTrie.CalculateRoot([tx]);
        header.Hash = header.CalculateHash();
        Block suggested = new(header, [tx], [], produced.Withdrawals) { BlockAccessList = list };
        BlockHeader parent = chain.BlockTree.FindHeader(produced.ParentHash!, BlockTreeLookupOptions.None)!;

        InvalidBlockException? exception = Assert.Catch<InvalidBlockException>(() =>
            chain.BranchProcessor.Process(parent, [suggested], ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance));

        // A valid transaction runs to completion sequentially, and the block is still rejected after processing.
        TransactionResult.ErrorType? expected = senderCase switch
        {
            SenderCase.Unfunded => TransactionResult.ErrorType.InsufficientMaxFeePerGasForSenderBalance,
            SenderCase.NonceTooHigh => TransactionResult.ErrorType.TransactionNonceTooHigh,
            _ => null,
        };
        Assert.That((exception as InvalidTransactionException)?.Reason.Error, Is.EqualTo(expected), exception!.Message);
    }

    [Test]
    public async Task Block_whose_list_omits_an_undeployed_system_contract_reports_it_empty([Values] bool parallelExecution)
    {
        // Built while EIP-8282 is off, the block's list leaves out the builder deposit contract, as the list of a block
        // that fails on its missing code does.
        ReleaseSpec withoutBuilderRequests = Amsterdam.Instance.Clone();
        withoutBuilderRequests.IsEip8282Enabled = false;
        TestSpecProvider specProvider = new(Amsterdam.Instance) { NextForkSpec = withoutBuilderRequests };
        using BasicTestBlockchain chain = await CreateChain(parallelExecution, builder => builder
            .AddSingleton<ISpecProvider>(specProvider)
            .WithGenesisPostProcessor((_, state) => state.DeleteAccount(Eip8282Constants.BuilderDepositRequestPredeployAddress)));
        Block produced = await chain.AddBlock();
        specProvider.NextForkSpec = Amsterdam.Instance;

        BlockHeader parent = chain.BlockTree.FindHeader(produced.ParentHash!, BlockTreeLookupOptions.None)!;
        Block suggested = produced.WithReplacedHeader(produced.Header.Clone());
        suggested.BlockAccessList = chain.Container.Resolve<IBlockAccessListStore>().Get(produced.Number, produced.Hash!);

        Action process = () => chain.BranchProcessor.Process(parent, [suggested], ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance);

        Assert.That(process, Throws.InstanceOf<InvalidBlockException>().With.Message.Contains(BlockErrorMessages.BuilderDepositsContractEmpty));
    }

    private static Task<BasicTestBlockchain> CreateChain(bool parallelExecution, Action<ContainerBuilder>? configure = null) =>
        BasicTestBlockchain.Create(builder =>
        {
            builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance))
                .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = parallelExecution);
            configure?.Invoke(builder);
        });
}
