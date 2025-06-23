// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using System.IO;
using Nethermind.Int256;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.CodeAnalysis.IL.Delegates;
using System.Reflection;
using Nethermind.Core.Crypto;
using System.Linq;
using Nethermind.Evm.Tracing.GethStyle;

namespace Nethermind.Evm.Test.ILEVM;

public class SyntheticBenchmarkTests
{

    [SetUp]
    public void SetUp()
    {
        AotContractsRepository.ClearCache();
        Precompiler.ResetEnvironment(true);

        Metrics.IlvmAotPrecompiledCalls = 0;
    }

    static byte[] fibbBytecode(byte[] argBytes) => Prepare.EvmCode
        .JUMPDEST()
        .PUSHx([0, 0])
        .POP()
        .PushData(argBytes)
        .COMMENT("1st/2nd fib number")
        .PushData(0)
        .PushData(1)
        .COMMENT("MAINLOOP:")
        .JUMPDEST()
        .DUPx(3)
        .ISZERO()
        .PushData(5 + 26 + argBytes.Length)
        .JUMPI()
        .COMMENT("fib step")
        .DUPx(2)
        .DUPx(2)
        .ADD()
        .SWAPx(2)
        .POP()
        .SWAPx(1)
        .COMMENT("decrement fib step counter")
        .SWAPx(2)
        .PushData(1)
        .SWAPx(1)
        .SUB()
        .SWAPx(2)
        .PushData(5 + 5 + argBytes.Length).COMMENT("goto MAINLOOP")
        .JUMP()
        .COMMENT("CLEANUP:")
        .JUMPDEST()
        .SWAPx(2)
        .POP()
        .POP()
        .SSTORE(0)
        .COMMENT("done: requested fib number is the only element on the stack!")
        .STOP()
        .Done;

    static byte[] isPrimeBytecode(byte[] argBytes) => Prepare.EvmCode
        .JUMPDEST()
        .PUSHx([0])
        .POP()
        .PUSHx(argBytes)
        .COMMENT("Store variable(n) in Memory")
        .MSTORE(0)
        .COMMENT("Store Indexer(i) in Memory")
        .PushData(2)
        .MSTORE(32)
        .COMMENT("We mark this place as a GOTO section")
        .JUMPDEST()
        .COMMENT("We check if i * i < n")
        .MLOAD(32)
        .DUPx(1)
        .MUL()
        .MLOAD(0)
        .LT()
        .PushData(4 + 47 + argBytes.Length)
        .JUMPI()
        .COMMENT("We check if n % i == 0")
        .MLOAD(32)
        .MLOAD(0)
        .MOD()
        .ISZERO()
        .DUPx(1)
        .COMMENT("if 0 we jump to the end")
        .PushData(4 + 51 + argBytes.Length)
        .JUMPI()
        .POP()
        .COMMENT("increment Indexer(i)")
        .MLOAD(32)
        .ADD(1)
        .MSTORE(32)
        .COMMENT("Loop back to top of conditional loop")
        .PushData(4 + 9 + argBytes.Length)
        .JUMP()
        .COMMENT("return 0")
        .JUMPDEST()
        .PushData(0)
        .SSTORE(0)
        .STOP()
        .JUMPDEST()
        .SSTORE(0)
        .STOP()
        .Done;

    static IEnumerable<UInt256> f_args => [1, 23, 101, 1023, 2047, 4999];
    static IEnumerable<UInt256> p_args => [1, 23, 1023, 8000009, 16000057];

    [Test, TestCaseSource(nameof(f_args))]
    public void fibbEquivalenceTest(UInt256 number)
    {
        byte[] bytes = new byte[32];
        number.ToBigEndian(bytes);
        var argBytes = bytes.WithoutLeadingZeros().ToArray();
        var bytecode = fibbBytecode(argBytes);

        IlVirtualMachineTestsBase standardChain = new IlVirtualMachineTestsBase(new VMConfig(), Prague.Instance);

        IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(new VMConfig
        {
            IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
            IlEvmAnalysisThreshold = 256,
            IlEvmAnalysisQueueMaxSize = 256,
            IlEvmPersistPrecompiledContractsOnDisk = false,
        }, Prague.Instance);


        byte[][] blobVersionedHashes = null;

        var address = standardChain.InsertCode(bytecode);
        enhancedChain.InsertCode(bytecode);

        var tracer = new GethLikeTxMemoryTracer(null, GethTraceOptions.Default);

        standardChain.Execute<ITxTracer>(bytecode, tracer, blobVersionedHashes: blobVersionedHashes);

        Assert.That(tracer.BuildResult().Failed, Is.False);

        Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.EqualTo(0));

        tracer = new GethLikeTxMemoryTracer(null, GethTraceOptions.Default);

        enhancedChain.Execute<ITxTracer>(bytecode, tracer, blobVersionedHashes: blobVersionedHashes);

        Assert.That(tracer.BuildResult().Failed, Is.False);


        var actual = standardChain.StateRoot;
        var expected = enhancedChain.StateRoot;

        Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.GreaterThan(0));
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test, TestCaseSource(nameof(p_args))]
    public void primEquivalenceTest(UInt256 number)
    {
        byte[] bytes = new byte[32];
        number.ToBigEndian(bytes);
        var argBytes = bytes.WithoutLeadingZeros().ToArray();
        var bytecode = isPrimeBytecode(argBytes);

        IlVirtualMachineTestsBase standardChain = new IlVirtualMachineTestsBase(new VMConfig(), Prague.Instance);

        IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(new VMConfig
        {
            IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
            IlEvmAnalysisThreshold = 256,
            IlEvmAnalysisQueueMaxSize = 256,
            IlEvmPersistPrecompiledContractsOnDisk = false,
        }, Prague.Instance);


        byte[][] blobVersionedHashes = null;

        var address = standardChain.InsertCode(bytecode);
        enhancedChain.InsertCode(bytecode);

        var tracer = new GethLikeTxMemoryTracer(null, GethTraceOptions.Default);

        standardChain.Execute<ITxTracer>(bytecode, tracer, blobVersionedHashes: blobVersionedHashes);

        Assert.That(tracer.BuildResult().Failed, Is.False);

        Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.EqualTo(0));

        tracer = new GethLikeTxMemoryTracer(null, GethTraceOptions.Default);

        enhancedChain.Execute<ITxTracer>(bytecode, tracer, blobVersionedHashes: blobVersionedHashes);

        Assert.That(tracer.BuildResult().Failed, Is.False);


        var actual = standardChain.StateRoot;
        var expected = enhancedChain.StateRoot;

        Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.GreaterThan(0));
        Assert.That(actual, Is.EqualTo(expected));
    }


    [Test, TestCaseSource(nameof(p_args))]
    public void fibbRountripTest(UInt256 number)
    {
        byte[] bytes = new byte[32];
        number.ToBigEndian(bytes);
        var argBytes = bytes.WithoutLeadingZeros().ToArray();
        var bytecode = fibbBytecode(argBytes);

        string path = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedContractsTests");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(new VMConfig
        {
            IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
            IlEvmAnalysisThreshold = 1,
            IlEvmAnalysisQueueMaxSize = 1,
            IlEvmContractsPerDllCount = 1,
            IlEvmPersistPrecompiledContractsOnDisk = true,
            IlEvmPrecompiledContractsPath = path,
        }, Prague.Instance);

        string fileName = Precompiler.GetTargetFileName();

        var address = enhancedChain.InsertCode(bytecode);

        enhancedChain.ForceRunAnalysis(address, ILMode.DYNAMIC_AOT_MODE);

        var assemblyPath = Path.Combine(path, fileName);

        Assembly assembly = Assembly.LoadFile(assemblyPath);
        MethodInfo method = assembly
            .GetTypes()
            .First(type => type.CustomAttributes.Any(attr => attr.AttributeType == typeof(NethermindPrecompileAttribute)))
            .GetMethod(nameof(ILEmittedMethod));
        Assert.That(method, Is.Not.Null);

        AotContractsRepository.ClearCache();
        var hashcode = Keccak.Compute(bytecode);

        AotContractsRepository.AddIledCode(hashcode, method.CreateDelegate<ILEmittedMethod>());
        Assert.That(AotContractsRepository.TryGetIledCode(hashcode, out var iledCode), Is.True, "AOT code is not found in the repository");

        enhancedChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, forceAnalysis: false);
        Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.GreaterThan(0));
    }

    [Test, TestCaseSource(nameof(p_args))]
    public void primRountripTest(UInt256 number)
    {
        byte[] bytes = new byte[32];
        number.ToBigEndian(bytes);
        var argBytes = bytes.WithoutLeadingZeros().ToArray();
        var bytecode = isPrimeBytecode(argBytes);

        string path = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedContractsTests");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(new VMConfig
        {
            IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
            IlEvmAnalysisThreshold = 1,
            IlEvmAnalysisQueueMaxSize = 1,
            IlEvmContractsPerDllCount = 1,
            IlEvmPersistPrecompiledContractsOnDisk = true,
            IlEvmPrecompiledContractsPath = path,
        }, Prague.Instance);

        string fileName = Precompiler.GetTargetFileName();

        var address = enhancedChain.InsertCode(bytecode);

        enhancedChain.ForceRunAnalysis(address, ILMode.DYNAMIC_AOT_MODE);

        var assemblyPath = Path.Combine(path, fileName);

        Assembly assembly = Assembly.LoadFile(assemblyPath);
        MethodInfo method = assembly
            .GetTypes()
            .First(type => type.CustomAttributes.Any(attr => attr.AttributeType == typeof(NethermindPrecompileAttribute)))
            .GetMethod(nameof(ILEmittedMethod));
        Assert.That(method, Is.Not.Null);

        AotContractsRepository.ClearCache();
        var hashcode = Keccak.Compute(bytecode);

        AotContractsRepository.AddIledCode(hashcode, method.CreateDelegate<ILEmittedMethod>());
        Assert.That(AotContractsRepository.TryGetIledCode(hashcode, out var iledCode), Is.True, "AOT code is not found in the repository");

        enhancedChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, forceAnalysis: false);
        Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.GreaterThan(0));
    }
}
