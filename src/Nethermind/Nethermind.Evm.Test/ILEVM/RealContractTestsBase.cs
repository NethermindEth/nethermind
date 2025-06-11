// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
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
                IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
                IlEvmContractsPerDllCount = 1,
                IsIlEvmAggressiveModeEnabled = true,
                IlEvmPersistPrecompiledContractsOnDisk = false,
            };
        }
        else
        {
            _config = new VMConfig();
        }
    }

    [SetUp]
    public override void Setup()
    {
        base.Setup();

        IlAnalyzer.StartPrecompilerBackgroundThread(_config, NullLogger.Instance);
        ILogManager logManager = GetLogManager();

        _blockhashProvider = new TestBlockhashProvider(SpecProvider);
        Machine = new VirtualMachine(_blockhashProvider, SpecProvider, CodeInfoRepository, logManager, _config);
        _processor = new TransactionProcessor(SpecProvider, TestState, Machine, CodeInfoRepository, logManager);
    }

    protected void ExecuteNoPrepare<T>(Block block, Transaction tx, T tracer, ForkActivation? fork = null,
        long gasAvailable = 10_000_000, byte[][] blobVersionedHashes = null, bool forceAnalysis = true)
        where T : ITxTracer
    {
        if (UseIlEvm && forceAnalysis)
        {
            ForceRunAnalysis(tx.To, ILMode.DYNAMIC_AOT_MODE);
        }

        var result = _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);

        result.Success.Should().BeTrue();
    }

    private void ForceRunAnalysis(Address address, ILMode mode)
    {
        var codeinfo = CodeInfoRepository.GetCachedCodeInfo(TestState, address, Prague.Instance, out _);

        if (codeinfo.IlInfo != null && codeinfo.IlInfo.AnalysisPhase == AnalysisPhase.Completed)
        {
            // Nothing to analyze, already prepared.
            return;
        }

        if (mode.HasFlag(ILMode.DYNAMIC_AOT_MODE))
        {
            IlAnalyzer.Analyse(codeinfo, ILMode.DYNAMIC_AOT_MODE, _config, NullLogger.Instance);
        }

        codeinfo.IlInfo.AnalysisPhase.Should().Be(AnalysisPhase.Completed);

        Precompiler.TryGetEmittedIL(codeinfo.IlInfo.PrecompiledContract!, out var ilInfo).Should().Be(true);
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

        IlAnalyzer.Analyse(codeinfo, ILMode.DYNAMIC_AOT_MODE, _config, NullLogger.Instance);

        codeinfo.IlInfo.AnalysisPhase.Should().Be(AnalysisPhase.Completed);

        Precompiler.TryGetEmittedIL(codeinfo.IlInfo.PrecompiledContract!, out var ilInfo).Should().Be(true);

        await Verifier.Verify(ilInfo);
    }

    public UInt256 GetAccountBalance(Address address)
    {
        return TestState.GetBalance(address);
    }

    public Address InsertCode(byte[] bytecode, Address? target = null)
    {
        var hashcode = Keccak.Compute(bytecode);
        var address = target ?? new Address(hashcode);

        TestState.CreateAccount(address, 1_000_000_000);
        TestState.InsertCode(address, hashcode, bytecode, Spec);
        return address;
    }

    protected void AssertBalance(in StorageCell cell, UInt256 expected)
    {
        ReadOnlySpan<byte> read = TestState.Get(cell);
        UInt256 after = new UInt256(read);
        after.Should().Be(expected);
    }

    [TearDown]
    protected void AssertIlevmCalls()
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
