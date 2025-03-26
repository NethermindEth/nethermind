// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Evm.CodeAnalysis.IL.Patterns;
using Nethermind.Core.Crypto;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;
using Nethermind.Db;
using Nethermind.Trie.Pruning;
using System.Diagnostics;
using Nethermind.Abi;
using System.Runtime.CompilerServices;
using Nethermind.Trie;

using static System.Runtime.CompilerServices.Unsafe;
using System.Reflection.Emit;
namespace Nethermind.Evm.Test.CodeAnalysis
{
    internal class AbortDestinationPattern : IPatternChunk // for testing
    {
        public string Name => nameof(AbortDestinationPattern);
        public byte[] Pattern => [(byte)Instruction.JUMPDEST, (byte)Instruction.STOP];
        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            return GasCostOf.JumpDest;
        }

        public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
        {
            if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
                result.ExceptionType = EvmExceptionType.OutOfGas;

            programCounter += 2;
            result.ContractState = ContractState.Finished;
        }
    }
    internal class SomeAfterTwoPush : IPatternChunk
    {
        public string Name => nameof(SomeAfterTwoPush);
        public byte[] Pattern => [96, 96, 01];

        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            long gasCost = GasCostOf.VeryLow * 3;
            return gasCost;
        }

        public void Invoke<T>(EvmState vmState, ulong chainId, in ExecutionEnvironment env, in TxExecutionContext txCtx, in BlockExecutionContext blkCtx, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack, ref ReadOnlyMemory<byte> returnDataBuffer, ITxTracer trace, ILogger logger, ref ILChunkExecutionState result) where T : struct, VirtualMachine.IIsTracing
        {
            if (!VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.NotOptimizing>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
                result.ExceptionType = EvmExceptionType.OutOfGas;

            UInt256 lhs = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1];
            UInt256 rhs = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 3];
            stack.PushUInt256(lhs + rhs);

            programCounter += 5;
        }
    }
    internal class TestBlockChain : VirtualMachineTestsBase
    {
        protected IVMConfig config;
        public TestBlockChain(IVMConfig config)
        {
            this.config = config;
            Setup();
        }

        public TestBlockChain()
        {
            config = new VMConfig();
            Setup();
        }
        public override void Setup()
        {
            base.Setup();

            ILogManager logManager = GetLogManager();

            _blockhashProvider = new TestBlockhashProvider(SpecProvider);
            Machine = new VirtualMachine(_blockhashProvider, SpecProvider, CodeInfoRepository, logManager, config);
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


            IlAnalyzer.AddPattern<SomeAfterTwoPush>();
            IlAnalyzer.AddPattern<EmulatedStaticCJump>();
            IlAnalyzer.AddPattern<EmulatedStaticJump>();
            IlAnalyzer.AddPattern<IsContractCheck>();
            IlAnalyzer.AddPattern<MethodSelector>();
            IlAnalyzer.AddPattern<AbortDestinationPattern>();

            IlAnalyzer.AddPattern<PP>();
            //IlAnalyzer.AddPattern<P01P01>();
            //IlAnalyzer.AddPattern<P01ADD>();
            IlAnalyzer.AddPattern<PJ>();
            IlAnalyzer.AddPattern<S01P>();
            IlAnalyzer.AddPattern<S02P>();
            IlAnalyzer.AddPattern<P01P01SHL>();
            IlAnalyzer.AddPattern<D01P04EQ>();
            IlAnalyzer.AddPattern<D01P04GT>();
            IlAnalyzer.AddPattern<D02MST>();
            IlAnalyzer.AddPattern<P01D02>();
            IlAnalyzer.AddPattern<P01D03>();
            IlAnalyzer.AddPattern<S02S01>();
        }

        public void Execute<T>(byte[] bytecode, T tracer, ForkActivation? fork = null, long gasAvailable = 1_000_000)
            where T : ITxTracer
        {
            var blobhashesCount = 10;
            var blobVersionedHashes = new byte[blobhashesCount][];
            for (int i = 0; i < blobhashesCount; i++)
            {
                blobVersionedHashes[i] = new byte[32];
                for (int n = 0; n < blobhashesCount; n++)
                {
                    blobVersionedHashes[i][n] = (byte)((i * 3 + 10 * 7) % 256);
                }
            }
            Execute<T>(tracer, bytecode, fork ?? MainnetSpecProvider.PragueActivation, gasAvailable, blobVersionedHashes);
        }

        public Address InsertCode(byte[] bytecode, Address? target = null)
        {
            var hashcode = Keccak.Compute(bytecode);
            var address = target ?? new Address(hashcode);

            var spec = Prague.Instance;
            TestState.CreateAccount(address, 1_000_000_000);
            TestState.InsertCode(address, bytecode, spec);
            return address;
        }
        public void ForceRunAnalysis(Address address, int mode)
        {
            var codeinfo = CodeInfoRepository.GetCachedCodeInfo(TestState, address, Prague.Instance, out _);

            if (mode.HasFlag(ILMode.PATTERN_BASED_MODE))
            {
                IlAnalyzer.Analyse(codeinfo, ILMode.PATTERN_BASED_MODE, config, NullLogger.Instance);
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

    }

    [TestFixture]
    [NonParallelizable]
    internal class IlEvmTests
    {
        private const string PatternField = "_patterns";
        private const int RepeatCount = 64;


        public static IEnumerable<(string, byte[])> GetPatBytecodesSamples()
        {

            yield return (nameof(D01P04EQ), Prepare.EvmCode
                     .PUSHx([1, 2, 3, 4])
                     .DUPx(1)
                     .PUSHx([1, 2, 3, 4])
                     .EQ()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(D01P04GT), Prepare.EvmCode
                     .PUSHx([1, 2, 3, 4])
                     .DUPx(1)
                     .PUSHx([1, 2, 3, 5])
                     .GT()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(D02MST), Prepare.EvmCode
                     .PUSHx([1])
                     .PUSHx([3])
                     .PUSHx([3])
                     .POP() //to avooid PUSHxDUPx pattern detection
                     .DUPx(2)
                     .MSTORE()
                     .POP()
                     .MLOAD(1)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(P01D03), Prepare.EvmCode
                     .PUSHx([5])
                     .PUSHx([1])
                     .PUSHx([3])
                     .DUPx(3)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(P01D02), Prepare.EvmCode
                     .PUSHx([1])
                     .PUSHx([3])
                     .DUPx(2)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(S02S01), Prepare.EvmCode
                     .PUSHx([5])
                     .PUSHx([1])
                     .PUSHx([3])
                     .SWAPx(2)
                     .SWAPx(1)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(S02P), Prepare.EvmCode
                     .PUSHx([5])
                     .PUSHx([1])
                     .PUSHx([3])
                     .SWAPx(2)
                     .POP()
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(S01P), Prepare.EvmCode
                     .PUSHx([2])
                     .PUSHx([3])
                     .SWAPx(1)
                     .POP()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(PJ), Prepare.EvmCode
                    .PUSHx([23])
                    .PUSHx([13])
                    .PUSHx([9])
                    .POP()
                    .JUMP()
                    .JUMPDEST()
                    .PushSingle(3)
                    .MUL()
                    .STOP()
                    .JUMPDEST()
                    .JUMP(8)
                    .Done);
            yield return (nameof(P01P01SHL), Prepare.EvmCode
                     .PUSHx([2])
                     .PUSHx([3])
                     .Op(Instruction.SHL)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(PP), Prepare.EvmCode
                     .PushData(((UInt256)3).PaddedBytes(32))
                     .PushData(((UInt256)4).PaddedBytes(32))
                     .PushData(((UInt256)5).PaddedBytes(32))
                     .POP()
                     .POP()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (nameof(EmulatedStaticCJump), Prepare.EvmCode
                    .PUSHx([1])
                    .PUSHx([0, 8])
                    .JUMPI()
                    .INVALID()
                    .JUMPDEST()
                    .Done);
            yield return (nameof(EmulatedStaticJump), Prepare.EvmCode
                    .PUSHx([0, 6])
                    .JUMP()
                    .INVALID()
                    .JUMPDEST()
                    .Done);
            yield return (nameof(MethodSelector), Prepare.EvmCode
                    .PushData(23)
                    .PushData(32)
                    .MSTORE()
                    .CALLVALUE()
                    .DUPx(1)
                    .PushData(32)
                    .MLOAD()
                    .SSTORE()
                    .Done);
            yield return (nameof(IsContractCheck), Prepare.EvmCode
                    .ADDRESS()
                    .EXTCODESIZE()
                    .DUPx(1)
                    .ISZERO()
                    .SSTORE()
                    .Done);
        }

        [Test]
        public void Execution_Swap_Happens_When_Pattern_Occurs()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                IlEvmEnabledMode = ILMode.PATTERN_BASED_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
            });

            byte[] bytecode =
                Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(1000)
                    .GAS()
                    .LT()
                    .PUSHx([0, 26])
                    .JUMPI()
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .POP()
                    .PUSHx([0, 0])
                    .JUMP()
                    .JUMPDEST()
                    .STOP()
                    .Done;

            for (int i = 0; i < RepeatCount; i++)
            {
                enhancedChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance);
            }

            Assert.That(Metrics.IlvmPredefinedPatternsExecutions, Is.GreaterThan(0));
        }

        [Test, TestCaseSource(nameof(GetPatBytecodesSamples))]
        public void ILVM_Pat_Execution_Equivalence_Tests((string opcode, byte[] bytecode) testcase)
        {
            TestBlockChain standardChain = new TestBlockChain(new VMConfig());
            var address = standardChain.InsertCode(testcase.bytecode);
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                IlEvmEnabledMode = ILMode.PATTERN_BASED_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
            });

            enhancedChain.InsertCode(testcase.bytecode);

            var bytecode =
                Prepare.EvmCode
                    .Call(address, 100000)
                    .STOP()
                    .Done;

            standardChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, (ForkActivation)10000000000);

            enhancedChain.ForceRunAnalysis(address, ILMode.PATTERN_BASED_MODE);

            enhancedChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, (ForkActivation)10000000000);

            var actual = enhancedChain.StateRoot;
            var expected = standardChain.StateRoot;

            Assert.That(Metrics.IlvmPredefinedPatternsExecutions, Is.GreaterThan(0));
            Assert.That(actual, Is.EqualTo(expected), testcase.opcode);
        }

        [Test]
        public void ILAnalyzer_Initialize_Add_All_Patterns()
        {
            IlAnalyzer.Initialize();
            // get static field _patterns from IlAnalyzer
            var patterns = typeof(IlAnalyzer).GetField(PatternField, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) as Dictionary<Type, IPatternChunk>;

            Assert.That(patterns.Count, Is.GreaterThan(0));
        }
    }
}
