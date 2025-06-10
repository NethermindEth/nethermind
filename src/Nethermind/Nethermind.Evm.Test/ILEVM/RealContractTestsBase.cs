// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

namespace Nethermind.Evm.Test.ILEVM;

public abstract class RealContractTestsBase : VirtualMachineTestsBase
{
    private bool UseIlEvm { get; }

    protected abstract byte[]? Bytecode { get; }

    private readonly IVMConfig _config;

    protected override ISpecProvider SpecProvider { get; set; } = new CustomSpecProvider((new ForkActivation(0, 0), Prague.Instance));

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

    protected void ExecuteNoPrepare<T>(Block block, Transaction tx, T tracer, ForkActivation? fork = null, long gasAvailable = 10_000_000, byte[][] blobVersionedHashes = null, bool forceAnalysis = true)
        where T : ITxTracer
    {
        if (UseIlEvm && forceAnalysis)
        {
            ForceRunAnalysis(tx.To, ILMode.DYNAMIC_AOT_MODE);
        }

        _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);
    }

    private void ForceRunAnalysis(Address address, ILMode mode)
    {
        var codeinfo = CodeInfoRepository.GetCachedCodeInfo(TestState, address, Prague.Instance, out _);

        if (mode.HasFlag(ILMode.DYNAMIC_AOT_MODE))
        {
            IlAnalyzer.Analyse(codeinfo, ILMode.DYNAMIC_AOT_MODE, _config, NullLogger.Instance);
        }

        codeinfo.IlInfo.AnalysisPhase = AnalysisPhase.Completed;

        // if (Precompiler.TryGetEmittedIL(codeinfo.IlInfo.PrecompiledContract!, out var ilInfo))
        // {
        // }
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
    [TearDown]
    protected void AssertIlevmCalls()
    {
        if (UseIlEvm)
        {
            Metrics.IlvmAotPrecompiledCalls.Should().BeGreaterThan(0, "The WrappedEth contract should be executed by the IL EVM");
        }
        else
        {
            Metrics.IlvmAotPrecompiledCalls.Should().Be(0, "The WrappedEth contract should not be executed by the IL EVM");
        }
    }
}
