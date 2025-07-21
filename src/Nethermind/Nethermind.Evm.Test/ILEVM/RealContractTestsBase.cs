// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
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
                IlEvmEnabledMode = ILMode.NO_ILVM
            };
        }
    }

    [SetUp]
    public override void Setup()
    {
        base.Setup();

        IlAnalyzer.StartPrecompilerBackgroundThread(_config, NullLogger.Instance);
        ILogManager logManager = GetLogManager();

        _blockhashProvider = new TestBlockhashProvider(SpecProvider);
        Machine = new VirtualMachine(_blockhashProvider, SpecProvider, logManager, _config);
        _processor = new TransactionProcessor(SpecProvider, TestState, Machine, CodeInfoRepository, logManager);
    }

    protected void ExecuteNoPrepare<T>(Block block, Transaction tx, T tracer, ForkActivation? fork = null,
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
