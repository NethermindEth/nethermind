// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;
using VerifyNUnit;

namespace Nethermind.Evm.Test.ILEVM;

public abstract class RealContractTestsBase : VirtualMachineTestsBase
{
    /// <summary>
    /// The contract address used for insertion of the code.
    /// </summary>
    protected static readonly Address ContractAddress = SenderRecipientAndMiner.Default.Recipient;

    protected abstract byte[] ByteCode { get; }

    protected IWorldState ControlState; // never exposed to ilevm

    private bool UseIlEvm { get; }

    private readonly IVMConfig _config;



    protected override ISpecProvider SpecProvider { get; set; } =
        new CustomSpecProvider((new ForkActivation(0, 0), Prague.Instance));

    static RealContractTestsBase()
    {
        // Memoize all IL for all the delegates.
        Precompiler.MemoizeILForSteps();
    }

    protected RealContractTestsBase(bool useIlEVM)
    {
        UseIlEvm = useIlEVM;

        if (useIlEVM)
        {
            _config = new VMConfig
            {
                IlEvmEnabledMode = ILMode.AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
                IlEvmContractsPerDllCount = 1,
                IlEvmPersistPrecompiledContractsOnDisk = false,
            };
        }
        else
        {
            _config = new VMConfig
            {
                IsILEvmEnabled = false,
                IlEvmEnabledMode = ILMode.NO_ILVM,
            };
        }
    }


    protected (Block block, Transaction transaction, Block controlBlock, Transaction controlTransaction) PrepareTxWithControl(ForkActivation activation, long gasLimit, byte[] code,
        byte[] input, UInt256 value, SenderRecipientAndMiner senderRecipientAndMiner = null)
    {

        senderRecipientAndMiner ??= SenderRecipientAndMiner.Default;
        (Block block, Transaction transaction) =  base.PrepareTx(activation, gasLimit, code, input, value);

        // checking if account exists - because creating new accounts overwrites already existing accounts,
        // thus overwriting storage roots - essentially clearing the storage slots
        // earlier it used to work - because the cache mapping address:storageTree was never cleared on account of
        // TestState.CommitTrees() not being called. But now the WorldState.CommitTrees which also calls TestState.CommitTrees, clearing the cache.
        if (!ControlState.AccountExists(senderRecipientAndMiner.Sender))
            ControlState.CreateAccount(senderRecipientAndMiner.Sender, 100.Ether());
        else
            ControlState.AddToBalance(senderRecipientAndMiner.Sender, 100.Ether(), SpecProvider.GenesisSpec);

        if (!ControlState.AccountExists(senderRecipientAndMiner.Recipient))
            ControlState.CreateAccount(senderRecipientAndMiner.Recipient, 100.Ether());
        else
            ControlState.AddToBalance(senderRecipientAndMiner.Recipient, 100.Ether(), SpecProvider.GenesisSpec);
        ControlState.InsertCode(senderRecipientAndMiner.Recipient, code, SpecProvider.GenesisSpec);

        ControlState.Commit(SpecProvider.GenesisSpec);

        var ethereumEcdsa = new EthereumEcdsa(SpecProvider.ChainId);

        Transaction controlTransaction = Build.A.Transaction
            .WithGasLimit(gasLimit)
            .WithGasPrice(1)
            .WithNonce(ControlState.GetNonce(senderRecipientAndMiner.Sender))
            .WithData(input)
            .WithValue(value)
            .To(senderRecipientAndMiner.Recipient)
            .SignedAndResolved(ethereumEcdsa, senderRecipientAndMiner.SenderKey)
            .TestObject;

        Block controlBlock = BuildBlock(activation, senderRecipientAndMiner);
        return (block, transaction, controlBlock, controlTransaction);
    }

    [SetUp]
    public override void Setup()
    {
        base.Setup();

        IlAnalyzer.StartPrecompilerBackgroundThread(_config, NullLogger.Instance);
        ILogManager logManager = GetLogManager();

        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, logManager);
        ControlState = worldStateManager.GlobalWorldState;

        _blockhashProvider = new TestBlockhashProvider(SpecProvider);
        Machine = new VirtualMachine(_blockhashProvider, SpecProvider, logManager, _config);
        _processor = new TransactionProcessor(SpecProvider, TestState, Machine, CodeInfoRepository, logManager);
    }


    public ITransactionProcessor GetControlTxProcessor()
    {
            var config = new VMConfig
            {
                IsILEvmEnabled = false,
                IlEvmEnabledMode = ILMode.NO_ILVM,
            };
        ILogManager logManager = GetLogManager();
        var machine = new VirtualMachine(new TestBlockhashProvider(SpecProvider), SpecProvider, logManager, config);
        var processor = new TransactionProcessor(SpecProvider, ControlState, machine, CodeInfoRepository, logManager);
        return processor;
    }

    protected void ExecuteNoPrepare<T>(Block block, Transaction tx, Block controlBlock, Transaction controlTx, T tracer, ForkActivation? fork = null,
        long gasAvailable = 10_000_000, byte[][] blobVersionedHashes = null, bool forceAnalysis = true, Address? senderAddress = null)
        where T : ITxTracer
    {
        senderAddress =  senderAddress ?? SenderRecipientAndMiner.Default.Sender;

        var controlTxProcessor = GetControlTxProcessor();

        var controlResult = controlTxProcessor.Execute(controlTx, new BlockExecutionContext(controlBlock.Header, Spec), tracer);

        ControlState.Commit(SpecProvider.GenesisSpec);
        var controlSenderBalance = ControlState.GetBalance(senderAddress);

        if (UseIlEvm && forceAnalysis)
        {
            ForceRunAnalysis(tx.To, ILMode.AOT_MODE);
        }

        var result = _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);
        TestState.Commit(SpecProvider.GenesisSpec);

        var senderBalance = TestState.GetBalance(senderAddress);

        controlResult.Success.Should().BeTrue();
        result.Success.Should().BeTrue();

        senderBalance.Should().Be(controlSenderBalance, "sender balance should be the same as in control state");
    }

    protected void ExecuteNoPrepare<T>(Block block, Transaction tx, T tracer, ForkActivation? fork = null,
        long gasAvailable = 10_000_000, byte[][] blobVersionedHashes = null, bool forceAnalysis = true, Address? senderAddress = null)
        where T : ITxTracer
    {
        senderAddress =  senderAddress ?? SenderRecipientAndMiner.Default.Sender;


        if (UseIlEvm && forceAnalysis)
        {
            ForceRunAnalysis(tx.To, ILMode.AOT_MODE);
        }

        var result = _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);

        var senderBalance = TestState.GetBalance(senderAddress);

        result.Success.Should().BeTrue();
    }

    protected void ExecuteNoPrepare3<T>(Block block, Transaction tx, T tracer, ForkActivation? fork = null,
        long gasAvailable = 10_000_000, byte[][] blobVersionedHashes = null, bool forceAnalysis = true)
        where T : ITxTracer
    {


        if (UseIlEvm && forceAnalysis)
        {
            ForceRunAnalysis(tx.To, ILMode.AOT_MODE);
        }

        var result = _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);

        result.Success.Should().BeTrue();
    }

    private void ForceRunAnalysis(Address address, ILMode mode)
    {
        var codeinfo = CodeInfoRepository.GetCachedCodeInfo(TestState, address, Prague.Instance, out _);

        if (codeinfo is not CodeInfo ci) return;

        if (ci.IlMetadata != null && ci.IlMetadata.AnalysisPhase == AnalysisPhase.Completed)
        {
            // Nothing to analyze, already prepared.
            return;
        }

        if (mode.HasFlag(ILMode.AOT_MODE))
        {
            IlAnalyzer.Analyse(ci, ILMode.AOT_MODE, _config, NullLogger.Instance);
        }

        ci.IlMetadata.AnalysisPhase.Should().Be(AnalysisPhase.Completed);

        Precompiler.TryGetEmittedIL(ci.IlMetadata.PrecompiledContract!, out var ilInfo).Should().Be(true);
    }

    [Test]
    public async Task Verify()
    {
        if (UseIlEvm == false)
        {
            Assert.Ignore();
        }

        // Just to please the assert at the TearDown
        Metrics.IlvmAotPrecompiledCalls = 1;

        TestState.CreateAccount(ContractAddress, 100.Ether());
        TestState.InsertCode(ContractAddress, Keccak.MaxValue.ValueHash256, ByteCode, SpecProvider.GenesisSpec);
        var codeinfo = CodeInfoRepository.GetCachedCodeInfo(TestState, ContractAddress, Prague.Instance, out _);

        if (codeinfo is not CodeInfo ci) { return; }

        IlAnalyzer.Analyse(ci, ILMode.AOT_MODE, _config, NullLogger.Instance);

        ci.IlMetadata.AnalysisPhase.Should().Be(AnalysisPhase.Completed);

        Precompiler.TryGetEmittedIL(ci.IlMetadata.PrecompiledContract!, out var ilInfo).Should().Be(true);

        await Verifier.Verify(ilInfo);
    }

    [TearDown]
    protected virtual void AssertIlevmCalls()
    {
        if (UseIlEvm)
        {
            Metrics.IlvmAotPrecompiledCalls.Should()
                .BeGreaterThan(0, "The WrappedEth contract should be executed by the IL EVM");
        }
        else
        {
            Metrics.IlvmAotPrecompiledCalls.Should()
                .Be(0, "The WrappedEth contract should not be executed by the IL EVM");
        }
    }
}
