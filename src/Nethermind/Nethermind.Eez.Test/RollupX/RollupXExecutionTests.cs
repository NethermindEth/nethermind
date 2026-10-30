// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXExecutionTests
{
    private const ulong BlockGasLimit = 30_000_000;
    private static readonly byte[] InvalidCode = [(byte)Instruction.INVALID];
    private static readonly EezChainRules Rules = EezChainRules.RollupX(new RollupXSchedule(1, 5,
        new RollupXL1Reference(10200, 1, TestItem.KeccakA, 0, TestItem.KeccakB), 0));

    private ISpecProvider _specProvider = null!;
    private IWorldState _state = null!;
    private IDisposable _stateScope = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = new EezSpecProvider(new TestSpecProvider(Osaka.Instance), Rules);
        _state = TestWorldStateFactory.CreateForTest();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
    }

    [TearDown]
    public void TearDown() => _stateScope.Dispose();

    [TestCase(0UL, 16_777_216UL, TestName = "EmptyBlockGivesTheCap")]
    [TestCase(29_970_000UL, 30_000UL, TestName = "NearlyFullBlockGivesWhatIsLeft")]
    public void Execute_SystemTransaction_GetsWhatTheBlockHasLeftUpToTheCap(ulong gasUsedBefore, ulong expectedGas)
    {
        (EezTransactionProcessor processor, Block block) = Processor(gasUsedBefore);
        DeployEezl2(InvalidCode);

        TransactionResult result = processor.Execute(SystemTransaction(EezConstants.RollupXEezl2Address), new CallOutputTracer());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True, "precondition: the system transaction is not refused for a declared limit above what is left");
            Assert.That(block.Header.GasUsed - gasUsedBefore, Is.EqualTo(expectedGas), "a halted call uses its whole budget, which is min(left, cap)");
        }
    }

    [Test]
    public void Execute_SystemTransactionWithLessLeftThanIntrinsicGas_IsRefused()
    {
        (EezTransactionProcessor processor, _) = Processor(BlockGasLimit - 10_000);
        DeployEezl2(InvalidCode);

        TransactionResult result = processor.Execute(SystemTransaction(EezConstants.RollupXEezl2Address), new CallOutputTracer());

        Assert.That(result.TransactionExecuted, Is.False, "a budget below intrinsic gas makes the block invalid");
    }

    [Test]
    public void Execute_SystemTransactionToRollup0Manager_IsMalformed()
    {
        (EezTransactionProcessor processor, _) = Processor(0);
        DeployEezl2(InvalidCode);

        TransactionResult result = processor.Execute(SystemTransaction(EezConstants.Eezl2Address), new CallOutputTracer());

        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction), "a RollupX chain accepts only its own manager as target");
    }

    [Test]
    public void Decode_RollupXTarget_TakesTheCapAndOnlyRollupXAcceptsIt()
    {
        byte[] encoded = TxDecoder.Instance.Encode(SystemTransaction(EezConstants.RollupXEezl2Address), RlpBehaviors.SkipTypedWrapping).Bytes;

        Transaction decoded = TxDecoder.Instance.Decode(encoded, RlpBehaviors.SkipTypedWrapping)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.To, Is.EqualTo(EezConstants.RollupXEezl2Address));
            Assert.That(decoded.GasLimit, Is.EqualTo(EezChainRules.RollupXSystemTxGasCap), "the RollupX manager implies the cap, which execution narrows");
            Assert.That(decoded.HasSystemTransactionFields(Rules), Is.True);
            Assert.That(decoded.HasSystemTransactionFields(EezChainRules.Rollup0), Is.False, "a rollup0 chain refuses RollupX's manager");
        }
    }

    [Test]
    public void Decode_UnknownTarget_Throws()
    {
        Transaction transaction = SystemTransaction(EezConstants.RollupXEezl2Address);
        transaction.To = TestItem.AddressA;
        byte[] encoded = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

        Assert.Throws<RlpException>(() => TxDecoder.Instance.Decode(encoded, RlpBehaviors.SkipTypedWrapping));
    }

    [TestCase(0UL, 980_000_000UL, TestName = "EmptyParent")]
    [TestCase(BlockGasLimit, 1_020_000_000UL, TestName = "FullParent")]
    public void BaseFee_RollupXChain_MovesByOneFiftieth(ulong parentGasUsed, ulong expectedWei)
    {
        BlockHeader parent = Build.A.BlockHeader.WithGasLimit(BlockGasLimit).WithGasUsed(parentGasUsed).WithBaseFee(1.GWei).TestObject;
        IReleaseSpec spec = _specProvider.GetSpec(parent);

        UInt256 baseFee = BaseFeeCalculator.Calculate(parent, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.BaseFeeMaxChangeDenominator, Is.EqualTo((UInt256)50), "precondition: RollupX's denominator");
            Assert.That(baseFee, Is.EqualTo((UInt256)expectedWei), "the base fee moves by 1/50 of the gap to the 15M target");
        }
    }

    [Test]
    public void ProcessTransactions_BlockStart_ZeroesTheManagerAndSystemBalancesFirst()
    {
        IReleaseSpec spec = _specProvider.GetSpec(new ForkActivation(1));
        _state.CreateAccount(EezConstants.RollupXEezl2Address, 5);
        _state.InsertCode(EezConstants.RollupXEezl2Address, InvalidCode, spec);
        _state.CreateAccount(EezConstants.SystemAddress, 7);
        _state.CreateAccount(TestItem.AddressA, 9);
        _state.Commit(spec);
        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        UInt256? managerBalanceSeenByTransactions = null;
        inner.When(static i => i.ProcessTransactions(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<BlockReceiptsTracer>(), Arg.Any<CancellationToken>()))
            .Do(_ => managerBalanceSeenByTransactions = _state.GetBalance(EezConstants.RollupXEezl2Address));

        new ProtocolBalanceCleanup(inner, _state, _specProvider, Rules)
            .ProcessTransactions(Build.A.Block.WithNumber(1).TestObject, ProcessingOptions.None, new BlockReceiptsTracer());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(managerBalanceSeenByTransactions, Is.EqualTo(UInt256.Zero), "the burn runs before the first transaction");
            Assert.That(_state.AccountExists(EezConstants.RollupXEezl2Address), Is.True, "the manager keeps its code and only loses its ether");
            Assert.That(_state.AccountExists(EezConstants.SystemAddress), Is.False, "an emptied system account without a nonce is removed under EIP-161");
            Assert.That(_state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)9), "no other balance is touched");
        }
    }

    [Test]
    public void ProcessExecutionRequests_RollupXChain_ReadsNoRequestsEvenWithPredeployCode()
    {
        IReleaseSpec spec = _specProvider.GetSpec(new ForkActivation(1));
        _state.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0);
        _state.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, InvalidCode, spec);
        _state.Commit(spec);
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        Block block = Build.A.Block.WithNumber(1).TestObject;

        new ExecutionRequestsProcessor(transactionProcessor, EezExecutionRequests.For(Rules)).ProcessExecutionRequests(block, _state, [], spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Header.RequestsHash, Is.EqualTo(ExecutionRequestExtensions.EmptyRequestsHash), "the header still commits to no requests");
            Assert.That(block.ExecutionRequests, Is.Empty);
        }
        transactionProcessor.DidNotReceiveWithAnyArgs().Execute(default!, default!);
    }

    [Test]
    public void Resolve_RollupXRules_WiresEveryRollupXExecutionRule()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddScoped(static _ => Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>())
            .AddSingleton(Substitute.For<IHeaderValidator>())
            .AddModule(new EezModule(new EezConfig(), Rules))
            .Build();
        using ILifetimeScope scope = container.BeginLifetimeScope(processing => processing.AddSingleton(_state));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Resolve<IBeaconBlockRootHandler>(), Is.SameAs(NoBeaconRootsCall.Instance), "RollupX never calls the beacon roots contract");
            Assert.That(scope.Resolve<ExecutionRequestsOptions>().ReadsRequests, Is.False, "RollupX blocks carry no requests");
            Assert.That(scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>(), Is.InstanceOf<ProtocolBalanceCleanup>(), "every block starts with the burn");
            Assert.That(scope.Resolve<ISpecProvider>().GetSpec(new ForkActivation(1)).BaseFeeMaxChangeDenominator, Is.EqualTo((UInt256)50));
            Assert.That(scope.Resolve<IHeaderValidator>(), Is.InstanceOf<RollupXHeaderValidator>(), "headers are held to RollupX's fixed profile");
        }
    }

    [Test]
    public void ProcessExecutionRequests_BlockAccessListManager_ReusesRollupXRequestRulesAcrossBlocks()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddModule(new EezModule(new EezConfig(), Rules))
            .Build();
        using ILifetimeScope scope = container.BeginLifetimeScope(processing => processing.AddSingleton(_state));
        IBlockAccessListManager manager = scope.Resolve<IBlockAccessListManager>();

        for (ulong number = 1; number <= 2; number++)
        {
            Block block = Build.A.Block.WithNumber(number).TestObject;
            manager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.ProducingBlock);
            manager.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
            manager.Setup(block);
            Assert.That(manager.Enabled, Is.True, "precondition: the block uses the BAL execution path");

            manager.ProcessExecutionRequests(block, [], Amsterdam.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(block.Header.RequestsHash, Is.EqualTo(ExecutionRequestExtensions.EmptyRequestsHash),
                    "RollupX does not dequeue consensus requests through the cached processor");
                Assert.That(block.ExecutionRequests, Is.Empty, "the request policy survives processor reuse");
            }
        }
    }

    [Test]
    public void CreateStatelessEnvironment_RollupX_WiresRulesAndCapturesThePrefixAfterBurn()
    {
        using Witness witness = new()
        {
            State = new ArrayPoolList<byte[]>(0),
            Codes = new ArrayPoolList<byte[]>(0),
            Keys = new ArrayPoolList<byte[]>(0),
            Headers = new ArrayPoolList<byte[]>(0),
        };
        IReleaseSpec spec = _specProvider.GetSpec(new ForkActivation(1));
        _state.CreateAccount(EezConstants.RollupXEezl2Address, 5);
        _state.InsertCode(EezConstants.RollupXEezl2Address, InvalidCode, spec);
        _state.CreateAccount(EezConstants.SystemAddress, 7);
        _state.Commit(spec);
        _state.SubtractFromBalance(EezConstants.RollupXEezl2Address, 5, spec);
        _state.SubtractFromBalance(EezConstants.SystemAddress, 7, spec);
        _state.Commit(spec, commitRoots: true);
        _state.RecalculateStateRoot();
        Hash256 expectedPrefix = _state.StateRoot;
        _state.AddToBalance(EezConstants.RollupXEezl2Address, 5, spec);
        _state.CreateAccount(EezConstants.SystemAddress, 7);
        _state.Commit(spec);
        Hash256? capturedPrefix = null;
        IBlockProcessor.IBlockTransactionsExecutor probe = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        probe.When(static executor => executor.ProcessTransactions(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<BlockReceiptsTracer>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                _state.Commit(spec, commitRoots: true);
                _state.RecalculateStateRoot();
                capturedPrefix = _state.StateRoot;
            });
        EezStatelessExecutor executor = new(_specProvider, Rules, LimboLogs.Instance);
        StatelessBlockProcessingEnv env = executor.CreateEnvironment(witness, transactionsExecutorDecorator: (_, _) => probe);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.ExecutionRequestsOptions.ReadsRequests, Is.False);
            Assert.That(env.BeaconBlockRootHandlerFactory!(Substitute.For<ITransactionProcessor>(), _state), Is.SameAs(NoBeaconRootsCall.Instance));
        }
        IBlockProcessor.IBlockTransactionsExecutor decorated = env.TransactionsExecutorDecorator!(Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>(), _state);
        decorated.ProcessTransactions(Build.A.Block.WithNumber(1).TestObject, ProcessingOptions.None, new BlockReceiptsTracer());
        Assert.That(capturedPrefix, Is.EqualTo(expectedPrefix), "the pre-execution checkpoint decorator sees the independently constructed post-burn state");
    }

    private (EezTransactionProcessor Processor, Block Block) Processor(ulong gasUsedBefore)
    {
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        EezTransactionProcessor processor = new(Rules, BlobBaseFeeCalculator.Instance, _specProvider, _state, virtualMachine,
            new EthereumCodeInfoRepository(_state), LimboLogs.Instance);
        Block block = Build.A.Block.WithNumber(1).WithGasLimit(BlockGasLimit).WithBaseFeePerGas(1.GWei).TestObject;
        block.Header.GasUsed = gasUsedBefore;
        processor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)));
        return (processor, block);
    }

    private void DeployEezl2(byte[] code)
    {
        IReleaseSpec spec = _specProvider.GetSpec(new ForkActivation(1));
        _state.CreateAccount(EezConstants.RollupXEezl2Address, 0);
        _state.InsertCode(EezConstants.RollupXEezl2Address, code, spec);
        _state.CreateAccount(EezConstants.Eezl2Address, 0);
        _state.InsertCode(EezConstants.Eezl2Address, code, spec);
        _state.Commit(spec);
        _state.CommitTree(0);
    }

    private Transaction SystemTransaction(Address target)
    {
        Transaction tx = SystemTransactions.Create(_specProvider.ChainId);
        tx.To = target;
        tx.GasLimit = target == EezConstants.RollupXEezl2Address ? EezChainRules.RollupXSystemTxGasCap : EezConstants.SystemTxGasLimit;
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
