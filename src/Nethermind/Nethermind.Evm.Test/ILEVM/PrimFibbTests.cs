// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.CodeAnalysis.IL.Delegates;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Evm.Test.ILEVM;

static class Bytecodes
{
    public static byte[] FibbBytecode(UInt256 number)
    {
        byte[] bytes = new byte[32];
        number.ToBigEndian(bytes);
        var argBytes = bytes.WithoutLeadingZeros().ToArray();

        return Prepare.EvmCode
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
    }

    public static byte[] PrimBytecode(UInt256 number)
    {
        byte[] bytes = new byte[32];
        number.ToBigEndian(bytes);
        var argBytes = bytes.WithoutLeadingZeros().ToArray();

        return Prepare.EvmCode
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
            .COMMENT("We check if i * i < n + 1")
            .MLOAD(32)
            .DUPx(1)
            .MUL()
            .MLOAD(0)
            .ADD(1)
            .LT()
            .PushData(4 + 3 + 47 + argBytes.Length)
            .JUMPI()
            .COMMENT("We check if n % i == 0")
            .MLOAD(32)
            .MLOAD(0)
            .MOD()
            .ISZERO()
            .DUPx(1)
            .COMMENT("if 0 we jump to the end")
            .PushData(4 + 3 + 51 + argBytes.Length)
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
            .PushData(1)
            .SSTORE(0)
            .STOP()
            .JUMPDEST()
            .SSTORE(0)
            .STOP()
            .Done;
    }

}

[NonParallelizable]
[TestFixture(false, nameof(Bytecodes.PrimBytecode), 40001263ul, 1ul)]
[TestFixture(false, nameof(Bytecodes.PrimBytecode), 8000010ul, 0ul)]
[TestFixture(false, nameof(Bytecodes.FibbBytecode), 43ul, 701408733ul)]
[TestFixture(false, nameof(Bytecodes.FibbBytecode), 5ul, 8ul)]
[TestFixture(true, nameof(Bytecodes.PrimBytecode), 40001263ul, 1ul)]
[TestFixture(true, nameof(Bytecodes.PrimBytecode), 8000010ul, 0ul)]
[TestFixture(true, nameof(Bytecodes.FibbBytecode), 5ul, 8ul)]
[TestFixture(true, nameof(Bytecodes.FibbBytecode), 43ul, 701408733ul)]
public class SyntheticTest(bool useIlEvm, string benchmarkName, ulong number, ulong result) : RealContractTestsBase(useIlEvm)
{
    UInt256 Result = result;
    UInt256 Number = number;

    [SetUp]
    public void SetUp()
    {
        AotContractsRepository.ClearCache();
        Precompiler.ResetEnvironment(true);

        Metrics.IlvmAotPrecompiledCalls = 0;
    }

    // Represents the address
    private static readonly Address SenderAddress = SenderRecipientAndMiner.Default.Sender;
    private static readonly UInt256 ResultResultStorage = new(0, 0, 0, 0);
    private static readonly StorageCell ResultBalanceCell = new(ContractAddress, ResultResultStorage);
    protected override void AssertIlevmCalls()
    {

    }


    [Test]
    public void Test()
    {
        var primeContractKey = SenderRecipientAndMiner.Default.RecipientKey;
        var miner = SenderRecipientAndMiner.Default.MinerKey;

        TestState.CreateAccount(ContractAddress, 100.Ether());
        var hashcode = Keccak.Compute(ByteCode);
        TestState.InsertCode(ContractAddress, hashcode, ByteCode, SpecProvider.GenesisSpec);
        SenderRecipientAndMiner toPrime = new SenderRecipientAndMiner
        {
            SenderKey = SenderRecipientAndMiner.Default.SenderKey,
            RecipientKey = primeContractKey,
            MinerKey = miner,
        };


        (Block block1, Transaction tx) = PrepareTx(Activation, 1000000, ByteCode, [], 10000, toPrime);

        ExecuteNoPrepare(block1, tx, NullTxTracer.Instance, Activation, 1000000, null, true);
        AssertResult(ResultBalanceCell, Result);
    }

    private void AssertResult(in StorageCell cell, UInt256 expected)
    {
        ReadOnlySpan<byte> read = TestState.Get(cell);
        UInt256 after = new UInt256(read);
        after.Should().Be(expected);
    }

    static IEnumerable<UInt256> args => [1, 23, 101, 1023, 2047, 4999, 8000009, 16000057];

    [Test, TestCaseSource(nameof(args))]
    public void EquivalenceTest(UInt256 number)
    {
        var bytecode = ResolveBytecode(Number);

        IlVirtualMachineTestsBase standardChain = new IlVirtualMachineTestsBase(new VMConfig(), Prague.Instance);
        Path.Combine(Directory.GetCurrentDirectory(), "GeneratedContracts.dll");

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


        standardChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, blobVersionedHashes: blobVersionedHashes);
        Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.EqualTo(0));

        var actual = standardChain.StateRoot;

        enhancedChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, blobVersionedHashes: blobVersionedHashes);
        Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.GreaterThan(0));

        var expected = enhancedChain.StateRoot;

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test, TestCaseSource(nameof(args))]
    public void RoundtripTest(UInt256 number)
    {
        var bytecode = ResolveBytecode(Number);

        String path = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedContractsTests");
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

    }

    private byte[] ResolveBytecode(UInt256 Number) =>
        benchmarkName switch
        {
            nameof(Bytecodes.FibbBytecode) => Bytecodes.FibbBytecode(Number),
            nameof(Bytecodes.PrimBytecode) => Bytecodes.PrimBytecode(Number),
            _ => throw new ArgumentException($"Unknown benchmark name: {benchmarkName}")
        };

    protected override byte[] ByteCode => ResolveBytecode(Number);
}
