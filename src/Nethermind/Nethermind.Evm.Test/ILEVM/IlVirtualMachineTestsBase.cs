// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using System.IO;
using System.Linq;

namespace Nethermind.Evm.Test.ILEVM;


public class IlVirtualMachineTestsBase : VirtualMachineTestsBase
{
    internal bool UseIlEvm { get; init; }
    internal virtual byte[]? Bytecode { get; set; }


    protected IVMConfig _config;
    public IlVirtualMachineTestsBase(IVMConfig config, IReleaseSpec spec) : this(config.IsVmOptimizationEnabled)
    {
        _config = config;
        SpecProvider = new TestSpecProvider(spec);
        Setup();
    }

    public IlVirtualMachineTestsBase(bool useIlEVM)
    {
        UseIlEvm = useIlEVM;

        _config = new VMConfig
        {
            IlEvmEnabledMode = ILMode.FULL_AOT_MODE,
            IlEvmAnalysisThreshold = 1,
            IlEvmAnalysisQueueMaxSize = 1,
            IlEvmContractsPerDllCount = 1,
            IsIlEvmAggressiveModeEnabled = true,
            IlEvmPersistPrecompiledContractsOnDisk = false,
        };

        Setup();
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

        var code = Prepare.EvmCode
            .PushData(23)
            .PushData(7)
            .ADD()
            .MSTORE(0, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
            .RETURN(0, 32)
            .STOP().Done;
        InsertCode(code, Address.FromNumber((int)Instruction.RETURN));

        var returningCode = Prepare.EvmCode
                    .PushData(UInt256.MaxValue)
                    .PUSHx([0])
                    .MSTORE()
                    .Return(32, 0)
                    .STOP()
                    .Done;
        InsertCode(returningCode, Address.FromNumber((int)Instruction.RETURNDATASIZE));
    }

    public void Execute<T>(byte[] bytecode, T tracer, ForkActivation? fork = null, long gasAvailable = 10_000_000, byte[][] blobVersionedHashes = null, bool forceAnalysis = true)
        where T : ITxTracer
    {
        (Block block, Transaction transaction) = PrepareTx(fork ?? Activation, 100000, bytecode);
        Execute(transaction, tracer, fork ?? MainnetSpecProvider.PragueActivation, gasAvailable, blobVersionedHashes, forceAnalysis);
    }

    public void Execute<T>(Transaction tx, T tracer, ForkActivation? fork = null, long gasAvailable = 10_000_000, byte[][] blobVersionedHashes = null, bool forceAnalysis = true)
        where T : ITxTracer
    {
        if(UseIlEvm && forceAnalysis)
        {
            ForceRunAnalysis(tx.To, ILMode.FULL_AOT_MODE);
        }
        Execute(fork ?? MainnetSpecProvider.PragueActivation, tx);
    }

    public Address InsertCode(byte[] bytecode, Address? target = null)
    {
        var hashcode = Keccak.Compute(bytecode);
        var address = target ?? new Address(hashcode);

        TestState.CreateAccount(address, 1_000_000_000);
        TestState.InsertCode(address, hashcode, bytecode, Spec);
        return address;
    }
    public void ForceRunAnalysis(Address address, int mode)
    {
        var codeinfo = CodeInfoRepository.GetCachedCodeInfo(TestState, address, Prague.Instance, out _);

        if (mode.HasFlag(ILMode.FULL_AOT_MODE))
        {
            IlAnalyzer.Analyse(codeinfo, ILMode.FULL_AOT_MODE, _config, NullLogger.Instance);
        }

        codeinfo.IlInfo.AnalysisPhase = AnalysisPhase.Completed;
    }

    public Hash256 StateRoot
    {
        get
        {
            TestState.Commit(Spec);
            TestState.RecalculateStateRoot();
            return TestState.StateRoot;
        }
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
