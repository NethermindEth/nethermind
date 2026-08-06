// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Threading;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Regression tests for the <see href="https://eips.ethereum.org/EIPS/eip-7997">EIP-7997</see>
/// deterministic deployment factory installed on the fork activation block. The full cross-fork
/// transition and same-block-use scenarios are covered end-to-end by the execution-spec-tests
/// fixtures under <c>eip7997_deterministic_factory_predeploy</c>.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class Eip7997FactoryDeploymentTests
{
    private static readonly Address Factory = Eip7997Constants.FactoryAddress;
    private static readonly byte[] NonCanonicalCode = [0x00];

    [Test]
    public void Deploy_installs_factory_when_missing()
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);

        Eip7997FactoryDeployer.Deploy(state, Amsterdam.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetCode(Factory), Is.EqualTo(Eip7997Constants.Code));
            Assert.That(state.GetNonce(Factory), Is.EqualTo(1UL));
            Assert.That(state.GetBalance(Factory), Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void Deploy_resets_noncanonical_code_and_bumps_zero_nonce()
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Factory, new UInt256(1000), 0);
        state.InsertCode(Factory, NonCanonicalCode, Amsterdam.Instance);

        Eip7997FactoryDeployer.Deploy(state, Amsterdam.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetCode(Factory), Is.EqualTo(Eip7997Constants.Code));
            Assert.That(state.GetNonce(Factory), Is.EqualTo(1UL));
            Assert.That(state.GetBalance(Factory), Is.EqualTo(new UInt256(1000)));
        }
    }

    [Test]
    public void Deploy_resets_noncanonical_code_and_preserves_nonzero_nonce()
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Factory, new UInt256(1000), 7);
        state.InsertCode(Factory, NonCanonicalCode, Amsterdam.Instance);

        Eip7997FactoryDeployer.Deploy(state, Amsterdam.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetCode(Factory), Is.EqualTo(Eip7997Constants.Code));
            Assert.That(state.GetNonce(Factory), Is.EqualTo(7UL));
            Assert.That(state.GetBalance(Factory), Is.EqualTo(new UInt256(1000)));
        }
    }

    [Test]
    public void Deploy_is_noop_when_canonical_code_already_present()
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Factory, new UInt256(500), 5);
        state.InsertCode(Factory, Eip7997Constants.Code, Amsterdam.Instance);

        Eip7997FactoryDeployer.Deploy(state, Amsterdam.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetCode(Factory), Is.EqualTo(Eip7997Constants.Code));
            Assert.That(state.GetNonce(Factory), Is.EqualTo(5UL));
            Assert.That(state.GetBalance(Factory), Is.EqualTo(new UInt256(500)));
        }
    }

    [Test]
    public void Bal_records_factory_install_at_pre_execution_index()
    {
        ReadOnlyAccountChanges? factory = RunActivation(seedCanonicalFactory: false);

        Assert.That(factory, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory!.CodeChangeAtIndex(0)?.Code, Is.EqualTo(Eip7997Constants.Code));
            Assert.That(factory.NonceChangeAtIndex(0), Is.EqualTo(new NonceChange(0, 1)));
        }
    }

    [Test]
    public void Bal_records_access_only_when_factory_already_deployed()
    {
        ReadOnlyAccountChanges? factory = RunActivation(seedCanonicalFactory: true);

        // Mainnet case: the factory already carries canonical code, so the activation block only
        // records it as an accessed account with no changes.
        Assert.That(factory, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory!.CodeChanges, Is.Empty);
            Assert.That(factory.NonceChanges, Is.Empty);
            Assert.That(factory.BalanceChanges, Is.Empty);
            Assert.That(factory.StorageChanges, Is.Empty);
            Assert.That(factory.StorageReads, Is.Empty);
        }
    }

    [TestCase(false, true, TestName = "Installs on the activation block whose parent predates EIP-7997")]
    [TestCase(true, false, TestName = "Leaves the factory untouched once the parent is already on the fork")]
    public void BlockProcessor_installs_factory_only_on_the_activation_block(bool parentHasEip7997, bool expectInstalled)
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor txProcessor = Substitute.For<ITransactionProcessor>();

        // Minimal fork spec: EIP-7997 without EIP-7928 so the deploy takes the plain-state path and the
        // post-state is directly observable without decoding a block access list.
        IReleaseSpec blockSpec = new ReleaseSpec { IsEip7997Enabled = true, IsEip158Enabled = true, MaxCodeSize = long.MaxValue };
        IReleaseSpec parentSpec = parentHasEip7997 ? new ReleaseSpec { IsEip7997Enabled = true } : new ReleaseSpec();

        const ulong parentTimestamp = 100;
        const ulong blockTimestamp = 200;
        ISpecProvider specProvider = new CustomSpecProvider(
            (new ForkActivation(0, parentTimestamp), parentSpec),
            (new ForkActivation(0, blockTimestamp), blockSpec));

        BlockAccessListManager balManager = new(
            state, specProvider, Substitute.For<IBlockhashProvider>(), LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false }, new WithdrawalProcessorFactory(LimboLogs.Instance),
            static ws => new EthereumCodeInfoRepository(ws));
        IBlockProcessor.IBlockTransactionsExecutor executor = new BlockProcessor.ParallelBlockValidationTransactionsExecutor(
            new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(txProcessor), state),
            state, specProvider, balManager, LimboLogs.Instance);

        BlockHeader parentHeader = Build.A.BlockHeader.WithNumber(0).WithTimestamp(parentTimestamp).TestObject;
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parentHeader);

        BlockProcessor processor = new(
            specProvider, Substitute.For<IBlockValidator>(), NoBlockRewards.Instance, executor, state,
            NullReceiptStorage.Instance, new BeaconBlockRootHandler(txProcessor, state), Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance, new WithdrawalProcessor(state, LimboLogs.Instance), new ExecutionRequestsProcessor(txProcessor),
            balManager, blockFinder);

        Block block = Build.A.Block.WithNumber(1).WithTimestamp(blockTimestamp)
            .WithParentHash(parentHeader.Hash!).WithBaseFeePerGas(0).WithGasLimit(GasCostOf.Transaction * 4).TestObject;

        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        processor.ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, blockSpec, CancellationToken.None);

        byte[]? factoryCode = state.GetCode(Factory);
        if (expectInstalled)
        {
            Assert.That(factoryCode, Is.EqualTo(Eip7997Constants.Code));
        }
        else
        {
            Assert.That(factoryCode, Is.Null.Or.Empty);
        }
    }

    /// <summary>
    /// Drives the pre-execution factory install through the BAL manager exactly as
    /// <see cref="BlockProcessor"/> does on the activation block, then returns the factory's entry
    /// in the generated block access list.
    /// </summary>
    private static ReadOnlyAccountChanges? RunActivation(bool seedCanonicalFactory)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        TestSingleReleaseSpecProvider specProvider = new(Amsterdam.Instance);
        BlockAccessListManager balManager = new(
            stateProvider,
            specProvider,
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            static worldState => new EthereumCodeInfoRepository(worldState));

        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        if (seedCanonicalFactory)
        {
            stateProvider.CreateAccount(Factory, UInt256.Zero, 1);
            stateProvider.InsertCode(Factory, Eip7997Constants.Code, Amsterdam.Instance);
        }
        stateProvider.Commit(Amsterdam.Instance);
        stateProvider.CommitTree(0);

        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithValue(1)
            .WithGasPrice(0)
            .WithGasLimit(GasCostOf.Transaction)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithGasLimit(GasCostOf.Transaction * 4)
            .WithTransactions(tx)
            .TestObject;
        block.BlockAccessList = null;

        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            inner, stateProvider, specProvider, balManager, LimboLogs.Instance);

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        executor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
        balManager.Setup(block);

        balManager.DeployDeterministicFactory(Amsterdam.Instance);

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        executor.ProcessTransactions(block, ProcessingOptions.None, tracer, CancellationToken.None);
        balManager.SetBlockAccessList(block);

        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(balManager.GeneratedBlockAccessList);
        return Rlp.Decode<ReadOnlyBlockAccessList>(encoded).GetAccountChanges(Factory);
    }
}
