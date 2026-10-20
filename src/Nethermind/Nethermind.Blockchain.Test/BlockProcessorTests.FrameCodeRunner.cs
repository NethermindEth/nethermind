// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public partial class BlockProcessorTests
{
    private static readonly Address RunnerCallee = new("0xc0000000000000000000000000000000000000c1");

    [Test]
    public async Task Block_level_access_list_execution_offers_frames_to_the_runner_of_its_scope([Values] bool parallelExecution)
    {
        FrameOfferProbe probe = new();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance) { AllowTestChainOverride = false })
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = parallelExecution)
            // Registered for the whole node, so the main processing scope resolves it as a query environment resolves its own.
            .AddSingleton<FrameCodeRunner<EthereumGasPolicy>>(probe)
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(RunnerCallee, UInt256.Zero);
                state.InsertCode(RunnerCallee, Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done, specProvider.GenesisSpec);
            }));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await chain.AddBlock(Build.A.Transaction.WithTo(RunnerCallee).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        Assert.That(block.Transactions, Has.Length.EqualTo(1), "precondition: the call is included");

        // Processed again with the access list it carries, on the path the configuration selects.
        Block suggested = block.WithReplacedHeader(block.Header.Clone());
        suggested.BlockAccessList = chain.Container.Resolve<IBlockAccessListStore>().Get(block.Number, block.Hash!);
        IBlockAccessListManager balManager = ((MainProcessingContext)chain.MainProcessingContext).LifetimeScope.Resolve<IBlockAccessListManager>();
        probe.Clear();
        using (chain.MainWorldState.BeginScope(parent))
        {
            chain.BlockProcessor.ProcessOne(suggested, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(block.Header), CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(balManager.ParallelExecutionEnabled, Is.EqualTo(parallelExecution), "precondition: the block runs on the path the configuration selects");
            Assert.That(probe.OffersFor(RunnerCallee), Is.GreaterThan(0), "the transaction runs on a machine of the access list execution");
            Assert.That(probe.OffersFor(Eip7002Constants.WithdrawalRequestPredeployAddress), Is.GreaterThan(0), "and so does the system call that ends the block");
        }
    }
}
