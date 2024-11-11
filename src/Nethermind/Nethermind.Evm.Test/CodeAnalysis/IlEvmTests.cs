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
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;
using static Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript.Log;
using Nethermind.Evm.CodeAnalysis.IL.Patterns;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Polly;
using Nethermind.Core.Collections;
using Nethermind.Specs.Test;
using System.Threading;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    internal class AbortDestinationPattern : InstructionChunk // for testing
    {
        public string Name => nameof(AbortDestinationPattern);
        public byte[] Pattern => [(byte)Instruction.JUMPDEST, (byte)Instruction.STOP];
        public byte CallCount { get; set; } = 0;
        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            return GasCostOf.JumpDest;
        }

        public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
            ref int programCounter,
            ref long gasAvailable,
            ref EvmStack<T> stack,
            ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
        {
            CallCount++;

            if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
                result.ExceptionType = EvmExceptionType.OutOfGas;

            programCounter += 2;
            result.ShouldStop = true;
        }
    }
    internal class SomeAfterTwoPush : InstructionChunk
    {
        public string Name => nameof(SomeAfterTwoPush);
        public byte[] Pattern => [96, 96, 01];
        public byte CallCount { get; set; } = 0;

        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            long gasCost = GasCostOf.VeryLow * 3;
            return gasCost;
        }

        public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
            ref int programCounter,
            ref long gasAvailable,
            ref EvmStack<T> stack,
            ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
        {
            CallCount++;

            if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
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
            TestState.CreateAccount(Address.FromNumber(23), 1000000);
            TestState.InsertCode(Address.FromNumber(23), code, SpecProvider.GenesisSpec);

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
            Execute<T>(tracer, bytecode, fork ?? MainnetSpecProvider.PragueActivation, gasAvailable);
        }

        public Address InsertCode(byte[] bytecode)
        {
            var hashcode = Keccak.Compute(bytecode);
            var address = new Address(hashcode);

            var spec = Prague.Instance;
            TestState.CreateAccount(address, 1_000_000_000);
            TestState.InsertCode(address, bytecode, spec);
            return address;
        }
        public void ForceRunAnalysis(Address address, int mode)
        {
            var codeinfo = CodeInfoRepository.GetCachedCodeInfo(TestState, address, Prague.Instance);
            var initialILMODE = codeinfo.IlInfo.Mode;
            if(mode.HasFlag(ILMode.JIT_MODE))
            {
                IlAnalyzer.Analyse(codeinfo, ILMode.JIT_MODE, config, NullLogger.Instance);
            }

            if (mode.HasFlag(ILMode.PAT_MODE))
            {
                IlAnalyzer.Analyse(codeinfo, ILMode.PAT_MODE, config, NullLogger.Instance);
            }
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
    internal class IlEvmTests : TestBlockChain
    {
        private const string AnalyzerField = "_analyzer";
        private const string PatternField = "_patterns";
        private const int RepeatCount = 256;

        [SetUp]
        public override void Setup()
        {
            base.Setup();

            base.config = new VMConfig()
            {
                IsJitEnabled = true,
                IsPatternMatchingEnabled = true,
                AggressiveJitMode = true,
                BakeInTracingInJitMode = true,

                PatternMatchingThreshold = 4,
                JittingThreshold = 256,
            };

            CodeInfoRepository.ClearCache();
        }

        public static IEnumerable<(Type, byte[])> GetPatBytecodesSamples()
        {

            yield return (typeof(D01P04EQ), Prepare.EvmCode
                     .PUSHx([1, 2, 3, 4])
                     .DUPx(1)
                     .PUSHx([1, 2, 3, 4])
                     .EQ()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(D01P04GT), Prepare.EvmCode
                     .PUSHx([1, 2, 3, 4])
                     .DUPx(1)
                     .PUSHx([1, 2, 3, 5])
                     .GT()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(D02MST), Prepare.EvmCode
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
            yield return (typeof(P01D03), Prepare.EvmCode
                     .PUSHx([5])
                     .PUSHx([1])
                     .PUSHx([3])
                     .DUPx(3)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(P01D02), Prepare.EvmCode
                     .PUSHx([1])
                     .PUSHx([3])
                     .DUPx(2)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(S02S01), Prepare.EvmCode
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
            yield return (typeof(S02P), Prepare.EvmCode
                     .PUSHx([5])
                     .PUSHx([1])
                     .PUSHx([3])
                     .SWAPx(2)
                     .POP()
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(S01P), Prepare.EvmCode
                     .PUSHx([2])
                     .PUSHx([3])
                     .SWAPx(1)
                     .POP()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(PJ), Prepare.EvmCode
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
            yield return (typeof(P01P01SHL), Prepare.EvmCode
                     .PUSHx([2])
                     .PUSHx([3])
                     .Op(Instruction.SHL)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(PP), Prepare.EvmCode
                     .PushData(((UInt256)3).PaddedBytes(32))
                     .PushData(((UInt256)4).PaddedBytes(32))
                     .PushData(((UInt256)5).PaddedBytes(32))
                     .POP()
                     .POP()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(EmulatedStaticCJump), Prepare.EvmCode
                    .PUSHx([1])
                    .PUSHx([0, 8])
                    .JUMPI()
                    .INVALID()
                    .JUMPDEST()
                    .Done);
            yield return (typeof(EmulatedStaticJump), Prepare.EvmCode
                    .PUSHx([0, 6])
                    .JUMP()
                    .INVALID()
                    .JUMPDEST()
                    .Done);
            yield return (typeof(MethodSelector), Prepare.EvmCode
                    .PushData(23)
                    .PushData(32)
                    .MSTORE()
                    .CALLVALUE()
                    .DUPx(1)
                    .PushData(32)
                    .MLOAD()
                    .SSTORE()
                    .Done);
            yield return (typeof(IsContractCheck), Prepare.EvmCode
                    .ADDRESS()
                    .EXTCODESIZE()
                    .DUPx(1)
                    .ISZERO()
                    .SSTORE()
                    .Done);
        }

        public static IEnumerable<(Instruction?, byte[], EvmExceptionType, (bool, bool))> GetJitBytecodesSamplesGenerator(bool turnOnAmortization, bool turnOnAggressiveMode)
        {
            yield return (Instruction.PUSH32, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.ISZERO, Prepare.EvmCode
                    .ISZERO(7)
                    .PushData(7)
                    .SSTORE()
                    .ISZERO(0)
                    .PushData(1)
                    .SSTORE()
                    .ISZERO(UInt256.MaxValue)
                    .PushData(23)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SUB, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .SUB()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.ADD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.ADDMOD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .PushSingle(5)
                    .ADDMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MUL, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MUL()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.EXP, Prepare.EvmCode
                    .PushSingle(0)
                    .PushSingle(7)
                    .EXP()
                    .PushData(2)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.EXP, Prepare.EvmCode
                    .PushSingle(1)
                    .PushSingle(7)
                    .EXP()
                    .PushData(3)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.EXP, Prepare.EvmCode
                    .PushSingle(1)
                    .PushSingle(0)
                    .EXP()
                    .PushData(4)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.EXP, Prepare.EvmCode
                    .PushSingle(1)
                    .PushSingle(1)
                    .EXP()
                    .PushData(5)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MOD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.DIV, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .DIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE | Instruction.MLOAD, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE | Instruction.MLOAD, Prepare.EvmCode
                    .MSTORE(123, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE | Instruction.MLOAD, Prepare.EvmCode
                    .MSTORE(32, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE | Instruction.MLOAD, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)0).PaddedBytes(32))
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE | Instruction.MLOAD, Prepare.EvmCode
                    .MSTORE(123, ((UInt256)0).PaddedBytes(32))
                    .MLOAD(123)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE | Instruction.MLOAD, Prepare.EvmCode
                    .MSTORE(32, ((UInt256)0).PaddedBytes(32))
                    .MLOAD(32)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE8, Prepare.EvmCode
                    .MSTORE8(0, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE8, Prepare.EvmCode
                    .MSTORE8(123, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(123)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE8, Prepare.EvmCode
                    .MSTORE8(32, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(32)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE8, Prepare.EvmCode
                    .MSTORE8(0, UInt256.MaxValue.PaddedBytes(32))
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE8, Prepare.EvmCode
                    .MSTORE8(123, UInt256.MaxValue.PaddedBytes(32))
                    .MLOAD(123)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSTORE8, Prepare.EvmCode
                    .MSTORE8(32, UInt256.MaxValue.PaddedBytes(32))
                    .MLOAD(32)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MCOPY(32, 0, 32)
                    .MLOAD(32)
                    .MLOAD(0)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(123, ((UInt256)23).PaddedBytes(32))
                    .MCOPY(32, 123, 32)
                    .MLOAD(32)
                    .MLOAD(0)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(32, ((UInt256)23).PaddedBytes(32))
                    .MCOPY(32, 123, 32)
                    .MLOAD(32)
                    .MLOAD(0)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)0).PaddedBytes(32))
                    .MCOPY(32, 0, 32)
                    .MLOAD(32)
                    .MLOAD(0)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(123, ((UInt256)0).PaddedBytes(32))
                    .MCOPY(32, 123, 32)
                    .MLOAD(32)
                    .MLOAD(123)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(32, ((UInt256)0).PaddedBytes(32))
                    .MCOPY(0, 32, 32)
                    .MLOAD(32)
                    .MLOAD(0)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(32, ((UInt256)0).PaddedBytes(32))
                    .MCOPY(32, 32, 32)
                    .MLOAD(32)
                    .PushSingle((UInt256)0)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(32, ((UInt256)23).PaddedBytes(32))
                    .MCOPY(32, 32, 32)
                    .MLOAD(32)
                    .PushSingle((UInt256)23)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.EQ, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.GT, Prepare.EvmCode
                    .PushSingle(7)
                    .PushSingle(23)
                    .GT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.GT, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .GT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.GT, Prepare.EvmCode
                    .PushSingle(17)
                    .PushSingle(17)
                    .GT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.LT, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .LT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.LT, Prepare.EvmCode
                    .PushSingle(7)
                    .PushSingle(23)
                    .LT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.LT, Prepare.EvmCode
                    .PushSingle(17)
                    .PushSingle(17)
                    .LT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.NOT, Prepare.EvmCode
                    .PushSingle(1)
                    .NOT()
                    .PushData(1)
                    .SSTORE()
                    .PushSingle(0)
                    .NOT()
                    .PushData(1)
                    .SSTORE()
                    .PushSingle(UInt256.MaxValue)
                    .NOT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.BLOBHASH, Prepare.EvmCode
                    .PushSingle(0)
                    .BLOBHASH()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.BLOCKHASH, Prepare.EvmCode
                .BLOCKHASH(0)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.CALLDATACOPY, Prepare.EvmCode
                .CALLDATACOPY(0, 0, 32)
                .MLOAD(0)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.CALLDATALOAD, Prepare.EvmCode
                .CALLDATALOAD(0)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MSIZE, Prepare.EvmCode
                .MSIZE()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.GASPRICE, Prepare.EvmCode
                .GASPRICE()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.CODESIZE, Prepare.EvmCode
                .CODESIZE()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.PC, Prepare.EvmCode
                .PC()
                .PushData(1)
                .SSTORE()
                .PC()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.COINBASE, Prepare.EvmCode
                .COINBASE()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.TIMESTAMP, Prepare.EvmCode
                .TIMESTAMP()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.NUMBER, Prepare.EvmCode
                .NUMBER()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.GASLIMIT, Prepare.EvmCode
                .GASLIMIT()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.CALLER, Prepare.EvmCode
                .CALLER()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.ADDRESS, Prepare.EvmCode
                .ADDRESS()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.ORIGIN, Prepare.EvmCode
                .ORIGIN()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.CALLVALUE, Prepare.EvmCode
                .CALLVALUE()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.CHAINID, Prepare.EvmCode
                .CHAINID()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.GAS, Prepare.EvmCode
                .PushData(23)
                .PushData(46)
                .ADD()
                .POP()
                .GAS()
                .PushData(1)
                .SSTORE()
                .PushData(23)
                .PushData(46)
                .ADD()
                .POP()
                .GAS()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.RETURNDATASIZE, Prepare.EvmCode
                .RETURNDATASIZE()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.BASEFEE, Prepare.EvmCode
                .BASEFEE()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.RETURN, Prepare.EvmCode
                .StoreDataInMemory(0, [2, 3, 5, 7])
                .RETURN(0, 32)
                .MLOAD(0)
                .PushData(1)
                .SSTORE().Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.REVERT, Prepare.EvmCode
                .StoreDataInMemory(0, [2, 3, 5, 7])
                .REVERT(0, 32)
                .MLOAD(0)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.CALLDATASIZE, Prepare.EvmCode
                .CALLDATASIZE()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.JUMPI | Instruction.JUMPDEST, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(1)
                .JUMPI(9)
                .PushSingle(3)
                .JUMPDEST()
                .PushSingle(0)
                .MUL()
                .GAS()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));


            yield return (Instruction.JUMPI | Instruction.JUMPDEST, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(0)
                .JUMPI(9)
                .PushSingle(3)
                .JUMPDEST()
                .PushSingle(0)
                .MUL()
                .GAS()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.JUMP | Instruction.JUMPDEST, Prepare.EvmCode
                .PushSingle(23)
                .JUMP(14)
                .JUMPDEST()
                .PushSingle(3)
                .MUL()
                .GAS()
                .PUSHx([1])
                .SSTORE()
                .STOP()
                .JUMPDEST()
                .JUMP(5)
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SHL, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(1)
                .SHL()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SHL, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(32)
                .SHL()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SHR, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(1)
                .SHR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SHR, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(32)
                .SHR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SAR, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(0)
                .SAR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SAR, Prepare.EvmCode
                .PushSingle(0)
                .PushSingle(23)
                .SAR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SAR, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(17)
                .SAR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SAR, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle((UInt256)((Int256.Int256)(-1)))
                .SAR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SAR, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle((UInt256)((Int256.Int256)(-1)))
                .SAR()
                .PushSingle((UInt256)((Int256.Int256)(1)))
                .SAR()
                .PushSingle(23)
                .EQ()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.AND, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(1)
                .AND()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.AND, Prepare.EvmCode
                .PushSingle(0)
                .PushSingle(UInt256.MaxValue)
                .AND()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.AND, Prepare.EvmCode
                .PushSingle(UInt256.MaxValue)
                .PushSingle(0)
                .AND()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.OR, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(1)
                .OR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.OR, Prepare.EvmCode
                .PushSingle(0)
                .PushSingle(UInt256.MaxValue)
                .OR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.OR, Prepare.EvmCode
                .PushSingle(UInt256.MaxValue)
                .PushSingle(0)
                .OR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.XOR, Prepare.EvmCode
                .PushSingle(23)
                .PushSingle(1)
                .XOR()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SLT, Prepare.EvmCode
                .PushSingle(17)
                .PushData(23)
                .SLT()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SLT, Prepare.EvmCode
                .PushData(23)
                .PushSingle(17)
                .SLT()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SLT, Prepare.EvmCode
                .PushData(17)
                .PushSingle(17)
                .SLT()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SGT, Prepare.EvmCode
                .PushData(23)
                .PushData(17)
                .SGT()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SGT, Prepare.EvmCode
                .PushData(17)
                .PushData(17)
                .SGT()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SGT, Prepare.EvmCode
                .PushData(17)
                .PushData(23)
                .SGT()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.BYTE, Prepare.EvmCode
                .BYTE(0, ((UInt256)(23)).PaddedBytes(32))
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.BYTE, Prepare.EvmCode
                .BYTE(16, UInt256.MaxValue.PaddedBytes(32))
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.BYTE, Prepare.EvmCode
                .BYTE(16, ((UInt256)(23)).PaddedBytes(32))
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.JUMP | Instruction.JUMPDEST, Prepare.EvmCode
                .JUMP(31)
                .INVALID()
                // this assumes that the code segment is jumping to another segment beyond it's boundaries
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.LOG0, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .LOGx(0, 0, 64)
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.LOG1, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(TestItem.KeccakA.Bytes.ToArray())
                .LOGx(1, 0, 64)
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.LOG2, Prepare.EvmCode
                .PushData(SampleHexData2.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(TestItem.KeccakA.Bytes.ToArray())
                .PushData(TestItem.KeccakB.Bytes.ToArray())
                .LOGx(2, 0, 64)
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.LOG3, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(TestItem.KeccakA.Bytes.ToArray())
                .PushData(TestItem.KeccakB.Bytes.ToArray())
                .PushData(TestItem.KeccakC.Bytes.ToArray())
                .LOGx(3, 0, 64)
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.LOG4, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(TestItem.KeccakA.Bytes.ToArray())
                .PushData(TestItem.KeccakB.Bytes.ToArray())
                .PushData(TestItem.KeccakC.Bytes.ToArray())
                .PushData(TestItem.KeccakD.Bytes.ToArray())
                .LOGx(4, 0, 64)
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.TSTORE | Instruction.TLOAD, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .TSTORE()
                .PushData(7)
                .TLOAD()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SSTORE | Instruction.SLOAD, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .SSTORE()
                .PushData(7)
                .SLOAD()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.EXTCODESIZE, Prepare.EvmCode
                .EXTCODESIZE(Address.FromNumber(23))
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.EXTCODEHASH, Prepare.EvmCode
                .EXTCODEHASH(Address.FromNumber(23))
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.EXTCODECOPY, Prepare.EvmCode
                .EXTCODECOPY(Address.FromNumber(23), 0, 0, 32)
                .MLOAD(0)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.BALANCE, Prepare.EvmCode
                .BALANCE(Address.FromNumber(23))
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SELFBALANCE, Prepare.EvmCode
                .SELFBALANCE()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.INVALID, Prepare.EvmCode
                .INVALID()
                .PushData(0)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.BadInstruction, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.STOP, Prepare.EvmCode
                .STOP()
                .PushData(0)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.POP, Prepare.EvmCode
                .PUSHx()
                .POP()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.POP | Instruction.INVALID, Prepare.EvmCode
                .POP()
                .POP()
                .POP()
                .POP()
                .Done, EvmExceptionType.StackUnderflow, (turnOnAmortization, turnOnAggressiveMode));


            for (byte opcode = (byte)Instruction.DUP1; opcode <= (byte)Instruction.DUP16; opcode++)
            {
                int n = opcode - (byte)Instruction.DUP1 + 1;
                var test = Prepare.EvmCode;
                for (int i = 0; i < n; i++)
                {
                    test.PushData(i);
                }
                test.Op((Instruction)opcode)
                    .PushData(1)
                    .SSTORE();

                yield return ((Instruction)opcode, test.Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));
            }

            for (byte opcode = (byte)Instruction.PUSH0; opcode <= (byte)Instruction.PUSH32; opcode++)
            {
                int n = opcode - (byte)Instruction.PUSH0;
                byte[] args = n == 0 ? null : Enumerable.Range(0, n).Select(i => (byte)i).ToArray();

                yield return ((Instruction)opcode, Prepare.EvmCode.PUSHx(args)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));
            }

            for (byte opcode = (byte)Instruction.SWAP1; opcode <= (byte)Instruction.SWAP16; opcode++)
            {
                int n = opcode - (byte)Instruction.SWAP1 + 2;
                var test = Prepare.EvmCode;
                for (int i = 0; i < n; i++)
                {
                    test.PushData(i);
                }
                test.Op((Instruction)opcode)
                    .PushData(1)
                    .SSTORE();

                yield return ((Instruction)opcode, test.Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));
            }

            yield return (Instruction.SDIV, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .SDIV()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SMOD, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .SMOD()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.CODECOPY, Prepare.EvmCode
                .PushData(0)
                .PushData(32)
                .PushData(7)
                .CODECOPY()
                .MLOAD(0)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.MULMOD, Prepare.EvmCode
                .PushData(23)
                .PushData(3)
                .PushData(7)
                .MULMOD()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.KECCAK256, Prepare.EvmCode
                .MSTORE(0, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())
                .PushData(0)
                .PushData(16)
                .KECCAK256()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.PREVRANDAO, Prepare.EvmCode
                .PREVRANDAO()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.RETURNDATACOPY, Prepare.EvmCode
                .PushData(0)
                .PushData(32)
                .PushData(0)
                .RETURNDATACOPY()
                .MLOAD(0)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.BLOBBASEFEE, Prepare.EvmCode
                .Op(Instruction.BLOBBASEFEE)
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));
            
            yield return (Instruction.SIGNEXTEND, Prepare.EvmCode
                .PushData(1024)
                .PushData(16)
                .SIGNEXTEND()
                .PushData(1)
                .SSTORE()
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SIGNEXTEND, Prepare.EvmCode
                .PushData(255)
                .PushData(0)
                .Op(Instruction.SIGNEXTEND)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SIGNEXTEND, Prepare.EvmCode
                .PushData(255)
                .PushData(32)
                .Op(Instruction.SIGNEXTEND)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.SIGNEXTEND, Prepare.EvmCode
                .PushData(UInt256.MaxValue)
                .PushData(31)
                .Op(Instruction.SIGNEXTEND)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done, EvmExceptionType.None, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.INVALID, Prepare.EvmCode
                .JUMPDEST()
                .MUL(23, 3)
                .POP()
                .JUMP(0)
                .Done, EvmExceptionType.OutOfGas, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.INVALID, Prepare.EvmCode
                .JUMPDEST()
                .PUSHx()
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .JUMP(0)
                .Done, EvmExceptionType.StackOverflow, (turnOnAmortization, turnOnAggressiveMode));

            yield return (Instruction.INVALID, Prepare.EvmCode
                .JUMPDEST()
                .MUL(23)
                .JUMP(0)
                .Done, EvmExceptionType.StackUnderflow, (turnOnAmortization, turnOnAggressiveMode));
        }

        public static IEnumerable<(Instruction?, byte[], EvmExceptionType, (bool, bool))> GetJitBytecodesSamples()
        {
            (bool, bool)[] combinations = new[]
            {
                (false, false),
                (false, true),
                (true, false),
                (true, true)
            };

            foreach (var combination in combinations)
            {
                foreach (var sample in GetJitBytecodesSamplesGenerator(combination.Item1, combination.Item2))
                {
                    yield return sample;
                }
            }
        }

        [Test]
        public void All_Stateless_Opcodes_Are_Covered_in_JIT_Tests()
        {
            List<Instruction> instructions = System.Enum.GetValues<Instruction>().ToList();
            instructions.Remove(Instruction.MSTORE);
            instructions.Remove(Instruction.MLOAD);
            instructions.Remove(Instruction.SSTORE);
            instructions.Remove(Instruction.SLOAD);
            instructions.Remove(Instruction.TSTORE);
            instructions.Remove(Instruction.TLOAD);
            instructions.Remove(Instruction.JUMP);
            instructions.Remove(Instruction.JUMPI);
            instructions.Remove(Instruction.JUMPDEST);

            instructions.Add(Instruction.MSTORE | Instruction.MLOAD);
            instructions.Add(Instruction.TSTORE | Instruction.TLOAD);
            instructions.Add(Instruction.SSTORE | Instruction.SLOAD);
            instructions.Add(Instruction.JUMP | Instruction.JUMPDEST);
            instructions.Add(Instruction.JUMPI | Instruction.JUMPDEST);

            var tests = GetJitBytecodesSamples().Select(test => test.Item1);

            List<Instruction> notCovered = new List<Instruction>();
            foreach (var opcode in instructions)
            {
                if (!opcode.IsStateful() && !tests.Contains(opcode))
                {
                    notCovered.Add(opcode);
                }
            }

            Assert.That(notCovered.Count, Is.EqualTo(0), $"[{String.Join(", ", notCovered)}]");
        }

        [Test]
        public void Pattern_Analyzer_Find_All_Instance_Of_Pattern()
        {
            byte[] bytecode =
                Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .Done;

            CodeInfo codeInfo = new CodeInfo(bytecode, TestItem.AddressA);

            IlAnalyzer.Analyse(codeInfo, ILMode.PAT_MODE, config, NullLogger.Instance);

            codeInfo.IlInfo.Chunks.Count.Should().Be(2);
        }

        [Test]
        public void JIT_Analyzer_Compiles_stateless_bytecode_chunk()
        {
            byte[] bytecode =
                Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .Call(Address.FromNumber(23), 10000)
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .STOP()
                    .Done;

            CodeInfo codeInfo = new CodeInfo(bytecode, TestItem.AddressA);

            IlAnalyzer.Analyse(codeInfo, IlInfo.ILMode.JIT_MODE, config, NullLogger.Instance);

            codeInfo.IlInfo.Segments.Count.Should().Be(2);
        }

        [Test]
        public void Execution_Swap_Happens_When_Pattern_Occurs()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = 1,
                IsPatternMatchingEnabled = true,
                JittingThreshold = int.MaxValue,
                IsJitEnabled = false,
                AggressiveJitMode = false,
                AnalysisQueueMaxSize = 1,
            });

            var pattern1 = IlAnalyzer.GetPatternHandler<SomeAfterTwoPush>();
            var pattern2 = IlAnalyzer.GetPatternHandler<EmulatedStaticJump>();
            var pattern3 = IlAnalyzer.GetPatternHandler<EmulatedStaticCJump>();

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

            /*
            var hashcode = Keccak.Compute(bytecode);
            var address = new Address(hashcode);
            var spec = Prague.Instance;
            TestState.CreateAccount(address, 1000000);
            TestState.InsertCode(address, bytecode, spec);
            */
            var accumulatedTraces = new List<GethTxTraceEntry>();
            for (int i = 0; i < RepeatCount ; i++)
            {
                var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
                enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer);
                var traces = tracer.BuildResult().Entries.Where(tr => tr.IsPrecompiled is not null && !tr.IsPrecompiled.Value).ToList();
                accumulatedTraces.AddRange(traces);
            }

            Assert.That(accumulatedTraces.Count, Is.GreaterThan(0));
        }

        [Test]
        public void Execution_Swap_Happens_When_Segments_are_compiled()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = int.MaxValue,
                IsPatternMatchingEnabled = false,
                AnalysisQueueMaxSize = 1,
                JittingThreshold = 1,
                IsJitEnabled = true,
                AggressiveJitMode = false
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

            var accumulatedTraces = new List<GethTxTraceEntry>();
            for (int i = 0; i < RepeatCount; i++)
            {
                var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
                enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer);
                var traces = tracer.BuildResult().Entries.Where(tr => tr.IsPrecompiled is not null && tr.IsPrecompiled.Value).ToList();
                accumulatedTraces.AddRange(traces);
            }

            Assert.That(accumulatedTraces.Count, Is.GreaterThan(0));
        }

        [Test]
        public void All_Opcodes_Have_Metadata()
        {
            Instruction[] instructions = System.Enum.GetValues<Instruction>();
            foreach (var opcode in instructions)
            {
                Assert.That(OpcodeMetadata.Operations.ContainsKey(opcode), Is.True);
            }
        }

        [Test, TestCaseSource(nameof(GetJitBytecodesSamples))]
        public void ILVM_JIT_Execution_Equivalence_Tests((Instruction? opcode, byte[] bytecode, EvmExceptionType, (bool enableAmortization, bool enableAggressiveMode)) testcase)
        {
            TestBlockChain standardChain = new TestBlockChain(new VMConfig());
            var address = standardChain.InsertCode(testcase.bytecode);
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = int.MaxValue,
                IsPatternMatchingEnabled = false,
                BakeInTracingInJitMode = !testcase.Item4.enableAmortization,
                AggressiveJitMode = testcase.Item4.enableAggressiveMode,
                JittingThreshold = 1,
                AnalysisQueueMaxSize = 1,
                IsJitEnabled = true
            });
            enhancedChain.InsertCode(testcase.bytecode);

            GethTraceOptions tracerOptions = new GethTraceOptions
            {
                EnableMemory = true,
            };

            var tracer1 = new GethLikeTxMemoryTracer(tracerOptions);
            var tracer2 = new GethLikeTxMemoryTracer(tracerOptions);

            var bytecode = Prepare.EvmCode
                .Call(address, 750_000)
                .STOP()
                .Done;

            standardChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer1);

            enhancedChain.ForceRunAnalysis(address, ILMode.JIT_MODE);

            enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer2);

            var normal_traces = tracer1.BuildResult();
            var ilvm_traces = tracer2.BuildResult();

            var actual = standardChain.StateRoot;
            var expected = enhancedChain.StateRoot;

            var enhancedHasIlvmTraces = ilvm_traces.Entries.Where(tr => tr.SegmentID is not null).Any();
            var normalHasIlvmTraces = normal_traces.Entries.Where(tr => tr.SegmentID is not null).Any();

            if (testcase.opcode is not null)
            {
                Assert.That(enhancedHasIlvmTraces, Is.True);
                Assert.That(normalHasIlvmTraces, Is.False);
            }
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test, TestCaseSource(nameof(GetPatBytecodesSamples))]
        public void ILVM_Pat_Execution_Equivalence_Tests((Type opcode, byte[] bytecode) testcase)
        {
            TestBlockChain standardChain = new TestBlockChain(new VMConfig());
            var address = standardChain.InsertCode(testcase.bytecode);
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = 1,
                IsPatternMatchingEnabled = true,
                JittingThreshold = int.MaxValue,
                AnalysisQueueMaxSize = 1,
                IsJitEnabled = false
            });
            enhancedChain.InsertCode(testcase.bytecode);

            var tracer1 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            var tracer2 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);

            var bytecode =
                Prepare.EvmCode
                    .Call(address, 100000)
                    .STOP()
                    .Done;

            standardChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer1, (ForkActivation)10000000000);

            enhancedChain.ForceRunAnalysis(address, ILMode.PAT_MODE);

            enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer2, (ForkActivation)10000000000);

            var normal_traces = tracer1.BuildResult();
            var ilvm_traces = tracer2.BuildResult();

            var actual = standardChain.StateRoot;
            var expected = enhancedChain.StateRoot;

            var enhancedHasIlvmTraces = ilvm_traces.Entries.Where(tr => tr.SegmentID is not null).Any();
            var normalHasIlvmTraces = normal_traces.Entries.Where(tr => tr.SegmentID is not null).Any();

            if (testcase.opcode is not null)
            {
                Assert.That(enhancedHasIlvmTraces, Is.True);
                Assert.That(normalHasIlvmTraces, Is.False);
            }
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void JIT_Mode_Segment_Has_Jump_Into_Another_Segment_Agressive_Mode_On()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = 1,
                IsPatternMatchingEnabled = false,
                JittingThreshold = 1,
                AnalysisQueueMaxSize = 1,
                IsJitEnabled = true,
                AggressiveJitMode = true,
            });

            var aux = enhancedChain.InsertCode(Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .ADD()
                .STOP().Done);

            Address main = enhancedChain.InsertCode(
                Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(1000)
                    .GAS()
                    .LT()
                    .JUMPI(59)
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
                    .Call(aux, 100)
                    .POP()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .POP()
                    .JUMP(0)
                    .JUMPDEST()
                    .STOP()
                    .Done);

            byte[] driver =
                Prepare.EvmCode
                    .Call(main, 1_000_000)
                    .Done;

            enhancedChain.ForceRunAnalysis(main, ILMode.JIT_MODE);
            enhancedChain.ForceRunAnalysis(aux, ILMode.JIT_MODE);

            var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            enhancedChain.Execute(driver, tracer);
            var traces = tracer.BuildResult().Entries.Where(tr => tr.SegmentID is not null).ToList();


            // in the last stint gas is almost below 1000
            // it executes segment 0 (0..47)
            // then calls address 23 (segment 0..5 since it is precompiled as well)
            // then it executes segment 49..60 which ends in jump back to pc = 0
            // then it executes segment 0..46 again but this time gas is below 1000
            // it ends jumping to pc = 59 (which occurs in segment 49..60)
            // so the last segment executed is (49..60)

            string[] desiredTracePattern = new[]
            {
                $"ILEVM_PRECOMPILED_({main})[0..47]",
                $"ILEVM_PRECOMPILED_({aux})[0..5]",
                $"ILEVM_PRECOMPILED_({main})[49..60]",
                $"ILEVM_PRECOMPILED_({main})[0..47]",
                $"ILEVM_PRECOMPILED_({main})[49..60]",
            };

            string[] actualTracePattern = traces.TakeLast(5).Select(tr => tr.SegmentID).ToArray();
            Assert.That(actualTracePattern, Is.EqualTo(desiredTracePattern));
        }

        [Test]
        public void JIT_Mode_Segment_Has_Jump_Into_Another_Segment_Agressive_Mode_Off()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = 2,
                AnalysisQueueMaxSize = 1,
                IsPatternMatchingEnabled = true,
                JittingThreshold = 1,
                IsJitEnabled = true,
                AggressiveJitMode = false,
                BakeInTracingInJitMode = true
            });

            var aux = enhancedChain.InsertCode(Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .ADD()
                .STOP().Done);

            Address main = enhancedChain.InsertCode(
                Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(1000)
                    .GAS()
                    .LT()
                    .JUMPI(59)
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
                    .Call(aux, 100)
                    .POP()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .POP()
                    .JUMP(0)
                    .JUMPDEST()
                    .STOP()
                    .Done);

            byte[] driver =
                Prepare.EvmCode
                    .Call(main, 1_000_000)
                    .Done;

            enhancedChain.ForceRunAnalysis(main, ILMode.PAT_MODE | ILMode.JIT_MODE);

            enhancedChain.ForceRunAnalysis(aux, ILMode.PAT_MODE | ILMode.JIT_MODE);

            var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            enhancedChain.Execute(driver, tracer);
            var traces = tracer.BuildResult().Entries.Where(tr => tr.SegmentID is not null).ToList();

            // in the last stint gas is almost below 1000
            // it executes segment 0 (0..47)
            // then calls address 23 (segment 0..5 since it is precompiled as well)
            // then it executes segment 49..60 which ends in jump back to pc = 0
            // then it executes segment 0..47 again but this time gas is below 1000
            // it ends jumping to pc = 59 (which is index of AbortDestinationPattern)
            // so the last segment executed is AbortDestinationPattern

            string[] desiredTracePattern = new[]
            {
                $"ILEVM_PRECOMPILED_({main})[0..47]",
                $"ILEVM_PRECOMPILED_({aux})[0..5]",
                $"ILEVM_PRECOMPILED_({main})[49..60]",
                $"ILEVM_PRECOMPILED_({main})[0..47]",
                $"AbortDestinationPattern",
            };

            string[] actualTracePattern = traces.TakeLast(5).Select(tr => tr.SegmentID).ToArray();
            Assert.That(actualTracePattern, Is.EqualTo(desiredTracePattern));
        }

        [Test]
        public void JIT_Mode_Segment_Has_Jump_Into_Another_Segment_Agressive_Mode_On_Equiv()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = 1,
                IsPatternMatchingEnabled = false,
                JittingThreshold = 1,
                AnalysisQueueMaxSize = 1,
                IsJitEnabled = true,
                AggressiveJitMode = true,
            });

            TestBlockChain standardChain = new TestBlockChain(new VMConfig());

            var auxCode = Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .ADD()
                .STOP().Done;

            var aux = enhancedChain.InsertCode(auxCode);
            standardChain.InsertCode(auxCode);

            var maincode = Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(1000)
                    .GAS()
                    .LT()
                    .JUMPI(59)
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
                    .Call(aux, 100)
                    .POP()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .POP()
                    .JUMP(0)
                    .JUMPDEST()
                    .STOP()
                    .Done;

            Address main = enhancedChain.InsertCode(maincode);
            standardChain.InsertCode(maincode);

            var tracer1 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            var tracer2 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);

            var bytecode =
                Prepare.EvmCode
                    .Call(main, 100000)
                    .STOP()
                    .Done;

            standardChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer1, (ForkActivation)10000000000);

            enhancedChain.ForceRunAnalysis(main, ILMode.JIT_MODE);

            enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer2, (ForkActivation)10000000000);

            var normal_traces = tracer1.BuildResult();
            var ilvm_traces = tracer2.BuildResult();

            var actual = standardChain.StateRoot;
            var expected = enhancedChain.StateRoot;

            var enhancedHasIlvmTraces = ilvm_traces.Entries.Where(tr => tr.SegmentID is not null).Any();
            var normalHasIlvmTraces = normal_traces.Entries.Where(tr => tr.SegmentID is not null).Any();

            Assert.That(enhancedHasIlvmTraces, Is.True);
            Assert.That(normalHasIlvmTraces, Is.False);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void JIT_Mode_Segment_Has_Jump_Into_Another_Segment_Agressive_Mode_Off_Equiv()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = 2,
                AnalysisQueueMaxSize = 1,
                IsPatternMatchingEnabled = true,
                JittingThreshold = 1,
                IsJitEnabled = true,
                AggressiveJitMode = false,
                BakeInTracingInJitMode = true
            });


            TestBlockChain standardChain = new TestBlockChain(new VMConfig());

            var auxCode = Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .ADD()
                .STOP().Done;

            var aux = enhancedChain.InsertCode(auxCode);
            standardChain.InsertCode(auxCode);

            var maincode = Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(1000)
                    .GAS()
                    .LT()
                    .JUMPI(59)
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
                    .Call(aux, 100)
                    .POP()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .POP()
                    .JUMP(0)
                    .JUMPDEST()
                    .STOP()
                    .Done;

            Address main = enhancedChain.InsertCode(maincode);
            standardChain.InsertCode(maincode);

            var tracer1 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            var tracer2 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);

            var bytecode =
                Prepare.EvmCode
                    .Call(main, 100000)
                    .STOP()
                    .Done;

            standardChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer1, (ForkActivation)10000000000);

            enhancedChain.ForceRunAnalysis(main, ILMode.JIT_MODE);

            enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer2, (ForkActivation)10000000000);

            var normal_traces = tracer1.BuildResult();
            var ilvm_traces = tracer2.BuildResult();

            var actual = standardChain.StateRoot;
            var expected = enhancedChain.StateRoot;

            var enhancedHasIlvmTraces = ilvm_traces.Entries.Where(tr => tr.SegmentID is not null).Any();
            var normalHasIlvmTraces = normal_traces.Entries.Where(tr => tr.SegmentID is not null).Any();

            Assert.That(enhancedHasIlvmTraces, Is.True);
            Assert.That(normalHasIlvmTraces, Is.False);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void JIT_invalid_opcode_results_in_failure()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = 1,
                IsPatternMatchingEnabled = false,
                JittingThreshold = 1,
                IsJitEnabled = true,
                AnalysisQueueMaxSize = 1,
                AggressiveJitMode = false
            });

            Address main = enhancedChain.InsertCode(
                Prepare.EvmCode
                    .PUSHx() // PUSH0
                    .POP()
                    .STOP()
                    .Done);

            byte[] driver =
                Prepare.EvmCode
                    .Call(main, 1000)
                    .JUMPI(17)
                    .INVALID()
                    .JUMPDEST()
                    .STOP()
                    .Done;

            enhancedChain.ForceRunAnalysis(main, ILMode.JIT_MODE);
            var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            enhancedChain.Execute(driver, tracer, (ForkActivation?)(MainnetSpecProvider.ByzantiumBlockNumber, 0));
            var traces = tracer.BuildResult();

            var HasIlvmTraces = traces.Entries.Where(tr => tr.SegmentID is not null).Any();
            var hasFailed = traces.Failed;
            Assert.That(HasIlvmTraces, Is.True);
            Assert.That(hasFailed, Is.True);
        }

        [Test]
        public void Pure_Opcode_Emition_Coveraga()
        {
            Instruction[] instructions =
                System.Enum.GetValues<Instruction>()
                .Where(opcode => !opcode.IsStateful())
                .ToArray();


            List<(Instruction, Exception)> notYetImplemented = [];
            foreach (var instruction in instructions)
            {
                string name = $"ILEVM_TEST_{instruction}";
                OpcodeInfo opcode = new OpcodeInfo(0, instruction, null);
                try
                {
                    ILCompiler.CompileSegment(name, [opcode], [], config);
                }
                catch (NotSupportedException nse)
                {
                    notYetImplemented.Add((instruction, nse));
                }
                catch (Exception)
                {
                }
            }

            string missingOpcodes = String.Join("; ", notYetImplemented.Select(op => op.Item1.ToString()));
            Assert.That(notYetImplemented.Count == 0, $"{notYetImplemented.Count} opcodes missing: [{missingOpcodes}]");
        }


        [Test]
        public void ILAnalyzer_Initialize_Add_All_Patterns()
        {
            IlAnalyzer.Initialize();
            // get static field _patterns from IlAnalyzer
            var patterns = typeof(IlAnalyzer).GetField(PatternField, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) as Dictionary<Type, InstructionChunk>;

            Assert.That(patterns.Count, Is.GreaterThan(0));
        }

        [Test, TestCaseSource(nameof(GetJitBytecodesSamples))]
        public void Ensure_Evm_ILvm_Compatibility((Instruction? opcode, byte[] bytecode, EvmExceptionType exceptionType, (bool enableAmortization, bool enableAggressiveMode)) testcase)
        {
            var codeInfo = new CodeInfo(testcase.bytecode, TestItem.AddressA);
            var blkExCtx = new BlockExecutionContext(BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header);
            var txExCtx = new TxExecutionContext(blkExCtx, TestItem.AddressA, 23, [TestItem.KeccakH.Bytes.ToArray()], CodeInfoRepository);
            var envExCtx = new ExecutionEnvironment(codeInfo, Recipient, Sender, Contract, new ReadOnlyMemory<byte>([1, 2, 3, 4, 5, 6, 7]), txExCtx, 23, 7);
            var stack = new byte[1024 * 32];
            var inputBuffer = envExCtx.InputData;
            var returnBuffer =
                new ReadOnlyMemory<byte>(Enumerable.Range(0, 32)
                .Select(i => (byte)i).ToArray());

            TestState.CreateAccount(Address.FromNumber(1), 1000000);
            TestState.InsertCode(Address.FromNumber(1), testcase.bytecode, Prague.Instance);

            var state = new EvmState(
                750_000,
                new ExecutionEnvironment(codeInfo, Address.FromNumber(1), Address.FromNumber(1), Address.FromNumber(1), ReadOnlyMemory<byte>.Empty, txExCtx, 0, 0),
                ExecutionType.CALL,
                Snapshot.Empty);

            IVirtualMachine evm = typeof(VirtualMachine).GetField("_evm", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Machine) as IVirtualMachine;

            state.InitStacks();

            var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);

            ILEvmState iLEvmState = new ILEvmState(SpecProvider.ChainId, state, EvmExceptionType.None, 0, 100000, ref returnBuffer);
            var metadata = IlAnalyzer.StripByteCode(testcase.bytecode);
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", metadata.Item1, metadata.Item2, config);
            ctx.PrecompiledSegment(ref iLEvmState, _blockhashProvider, TestState, CodeInfoRepository, Prague.Instance, tracer, ctx.Data);

            Assert.That(iLEvmState.EvmException == testcase.exceptionType);
        }


        [Test, TestCaseSource(nameof(GetJitBytecodesSamples))]
        public void Test_ILVM_Trace_Mode((Instruction? opcode, byte[] bytecode, EvmExceptionType exceptionType, (bool enableAmortization, bool enableAggressiveMode)) testcase)
        {
            var codeInfo = new CodeInfo(testcase.bytecode, TestItem.AddressA);
            var blkExCtx = new BlockExecutionContext(BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header);
            var txExCtx = new TxExecutionContext(blkExCtx, TestItem.AddressA, 23, [TestItem.KeccakH.Bytes.ToArray()], CodeInfoRepository);
            var envExCtx = new ExecutionEnvironment(codeInfo, Recipient, Sender, Contract, new ReadOnlyMemory<byte>([1, 2, 3, 4, 5, 6, 7]), txExCtx, 23, 7);
            var stack = new byte[1024 * 32];
            var inputBuffer = envExCtx.InputData;
            var returnBuffer =
                new ReadOnlyMemory<byte>(Enumerable.Range(0, 32)
                .Select(i => (byte)i).ToArray());

            TestState.CreateAccount(Address.FromNumber(1), 1000000);
            TestState.InsertCode(Address.FromNumber(1), testcase.bytecode, Prague.Instance);

            var state = new EvmState(
                750_000,
                new ExecutionEnvironment(codeInfo, Address.FromNumber(1), Address.FromNumber(1), Address.FromNumber(1), ReadOnlyMemory<byte>.Empty, txExCtx, 0, 0),
                ExecutionType.CALL,
                Snapshot.Empty);

            IVirtualMachine evm = typeof(VirtualMachine).GetField("_evm", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Machine) as IVirtualMachine;

            state.InitStacks();

            var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);

            ILEvmState iLEvmState = new ILEvmState(SpecProvider.ChainId, state, EvmExceptionType.None, 0, 100000, ref returnBuffer);
            var metadata = IlAnalyzer.StripByteCode(testcase.bytecode);
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", metadata.Item1, metadata.Item2, config);
            ctx.PrecompiledSegment(ref iLEvmState, _blockhashProvider, TestState, CodeInfoRepository, Prague.Instance, tracer, ctx.Data);

            var tracedOpcodes = tracer.BuildResult().Entries;

            if (testcase.opcode is not null)
            {
                Assert.That(tracedOpcodes.Count, Is.GreaterThan(0));
            }
            else
            {
                Assert.That(tracedOpcodes.Count, Is.EqualTo(0));
            }
        }

        [Test, TestCaseSource(nameof(GetJitBytecodesSamples))]
        public void Test_ILVM_Trace_Mode_Has_0_Traces_When_TraceInstructions_Is_Off((Instruction? opcode, byte[] bytecode, EvmExceptionType exceptionType, (bool enableAmortization, bool enableAggressiveMode)) testcase)
        {
            var codeInfo = new CodeInfo(testcase.bytecode, TestItem.AddressA);
            var blkExCtx = new BlockExecutionContext(BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header);
            var txExCtx = new TxExecutionContext(blkExCtx, TestItem.AddressA, 23, [TestItem.KeccakH.Bytes.ToArray()], CodeInfoRepository);
            var envExCtx = new ExecutionEnvironment(codeInfo, Recipient, Sender, Contract, new ReadOnlyMemory<byte>([1, 2, 3, 4, 5, 6, 7]), txExCtx, 23, 7);
            var stack = new byte[1024 * 32];
            var inputBuffer = envExCtx.InputData;
            var returnBuffer =
                new ReadOnlyMemory<byte>(Enumerable.Range(0, 32)
                .Select(i => (byte)i).ToArray());

            TestState.CreateAccount(Address.FromNumber(1), 1000000);
            TestState.InsertCode(Address.FromNumber(1), testcase.bytecode, Prague.Instance);

            var state = new EvmState(
                750_000,
                new ExecutionEnvironment(codeInfo, Address.FromNumber(1), Address.FromNumber(1), Address.FromNumber(1), ReadOnlyMemory<byte>.Empty, txExCtx, 0, 0),
                ExecutionType.CALL,
                Snapshot.Empty);

            IVirtualMachine evm = typeof(VirtualMachine).GetField("_evm", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Machine) as IVirtualMachine;

            state.InitStacks();

            var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);

            ILEvmState iLEvmState = new ILEvmState(SpecProvider.ChainId, state, EvmExceptionType.None, 0, 100000, ref returnBuffer);
            var metadata = IlAnalyzer.StripByteCode(testcase.bytecode);
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", metadata.Item1, metadata.Item2, config);
            ctx.PrecompiledSegment(ref iLEvmState, _blockhashProvider, TestState, CodeInfoRepository, Prague.Instance, NullTxTracer.Instance, ctx.Data);

            var tracedOpcodes = tracer.BuildResult().Entries;

            state.Dispose();

            Assert.That(tracedOpcodes.Count, Is.EqualTo(0));
        }
    }
}
